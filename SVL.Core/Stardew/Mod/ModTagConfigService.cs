using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SVL.Core.Logging;

namespace SVL.Core.Stardew.Mod;

public sealed class ModTagConfig
{
    public List<ModCustomTagDefinition> CustomTags { get; set; } = [];
    public Dictionary<string, List<string>> Assignments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> FolderTagOrder { get; set; } = [];
    public List<string> CustomTagOrder { get; set; } = [];
}

public sealed class ModCustomTagDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public static class ModTagConfigService
{
    private const string ConfigFileName = ".svl-mod-tags.json";

    public static ModTagConfig Load(string modsPath)
    {
        try
        {
            var filePath = GetConfigPath(modsPath);
            if (!File.Exists(filePath))
                return new ModTagConfig();

            var json = File.ReadAllText(filePath);
            var config = JsonSerializer.Deserialize<ModTagConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            }) ?? new ModTagConfig();

            config.CustomTags ??= [];
            config.Assignments ??= new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            config.FolderTagOrder ??= [];
            config.CustomTagOrder ??= [];
            return config;
        }
        catch (Exception ex)
        {
            Log.Warn("[ModTagConfig] 读取标签配置失败，将使用空配置", ex);
            return new ModTagConfig();
        }
    }

    public static bool Save(string modsPath, ModTagConfig config)
    {
        try
        {
            Directory.CreateDirectory(modsPath);
            var filePath = GetConfigPath(modsPath);
            var normalized = Normalize(config);
            var json = JsonSerializer.Serialize(normalized, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filePath, json);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("[ModTagConfig] 保存标签配置失败", ex);
            return false;
        }
    }

    private static ModTagConfig Normalize(ModTagConfig config)
    {
        config ??= new ModTagConfig();
        config.CustomTags ??= [];
        config.Assignments ??= new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        config.FolderTagOrder ??= [];
        config.CustomTagOrder ??= [];

        config.CustomTags = config.CustomTags
            .Where(tag => !string.IsNullOrWhiteSpace(tag.Id) && !string.IsNullOrWhiteSpace(tag.Name))
            .GroupBy(tag => tag.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var validTagIds = config.CustomTags.Select(tag => tag.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        config.CustomTagOrder = config.CustomTagOrder
            .Where(tagId => !string.IsNullOrWhiteSpace(tagId) && validTagIds.Contains(tagId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var tag in config.CustomTags)
        {
            if (!config.CustomTagOrder.Any(id => string.Equals(id, tag.Id, StringComparison.OrdinalIgnoreCase)))
            {
                config.CustomTagOrder.Add(tag.Id);
            }
        }

        config.FolderTagOrder = config.FolderTagOrder
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var cleanedAssignments = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in config.Assignments)
        {
            var modKey = assignment.Key;
            var tags = assignment.Value;
            if (string.IsNullOrWhiteSpace(modKey) || tags == null)
                continue;

            var filtered = tags
                .Where(tagId => !string.IsNullOrWhiteSpace(tagId) && validTagIds.Contains(tagId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (filtered.Count > 0)
                cleanedAssignments[modKey] = filtered;
        }

        config.Assignments = cleanedAssignments;
        return config;
    }

    private static string GetConfigPath(string modsPath)
    {
        return Path.Combine(modsPath, ConfigFileName);
    }
}