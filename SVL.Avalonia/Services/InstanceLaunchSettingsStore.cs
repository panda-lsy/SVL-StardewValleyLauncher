using System.Collections.Concurrent;
using System.Text.Json;
using SVL.Avalonia.Models;

namespace SVL.Avalonia.Services;

/// <summary>按实际运行目录保存实例启动设置，旧版全局设置仅作为未单独配置时的回退。</summary>
public sealed class InstanceLaunchSettingsStore
{
    private static readonly ConcurrentDictionary<string, object> Locks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly string _path;
    private readonly object _gate;

    public InstanceLaunchSettingsStore(AppUserSettingsStore settingsStore)
    {
        _path = Path.Combine(Path.GetDirectoryName(settingsStore.GetSettingsPath())!, "instance-launch-settings.json");
        _gate = Locks.GetOrAdd(_path, static _ => new object());
    }

    public string GetSettingsPath() => _path;

    public InstanceLaunchSettings GetEffective(string? instancePath, AppUserSettings fallback)
    {
        if (string.IsNullOrWhiteSpace(instancePath)) return InstanceLaunchSettings.FromGlobal(fallback);
        lock (_gate)
        {
            var entries = ReadEntries();
            return entries.TryGetValue(NormalizePath(instancePath), out var settings)
                ? settings
                : InstanceLaunchSettings.FromGlobal(fallback);
        }
    }

    public void Save(string instancePath, InstanceLaunchSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instancePath);
        lock (_gate)
        {
            var entries = ReadEntries();
            entries[NormalizePath(instancePath)] = settings;
            WriteEntries(entries);
        }
    }

    /// <summary>只补充缺失的旧实例，绝不覆盖已经在新版里保存过的条目。</summary>
    public void ImportMissing(IEnumerable<KeyValuePair<string, InstanceLaunchSettings>> legacySettings)
    {
        lock (_gate)
        {
            var entries = ReadEntries();
            var changed = false;
            foreach (var entry in legacySettings)
            {
                if (!string.IsNullOrWhiteSpace(entry.Key) && entries.TryAdd(NormalizePath(entry.Key), entry.Value))
                    changed = true;
            }

            if (changed) WriteEntries(entries);
        }
    }

    private Dictionary<string, InstanceLaunchSettings> ReadEntries()
    {
        var result = new Dictionary<string, InstanceLaunchSettings>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (!File.Exists(_path)) return result;

        // 文件损坏或不可读时向调用方报告，不将它当空配置覆盖。
        var data = JsonSerializer.Deserialize<Dictionary<string, InstanceLaunchSettings>>(File.ReadAllText(_path), Options)
                   ?? throw new InvalidDataException("实例启动设置文件为空或格式无效");
        foreach (var entry in data)
        {
            if (!string.IsNullOrWhiteSpace(entry.Key) && entry.Value != null)
                result[NormalizePath(entry.Key)] = entry.Value;
        }
        return result;
    }

    private void WriteEntries(Dictionary<string, InstanceLaunchSettings> entries) =>
        AtomicFileWriter.WriteUtf8(_path, JsonSerializer.Serialize(entries, Options));

    private static string NormalizePath(string path)
    {
        var normalized = Path.GetFullPath(path.Trim().Trim('"'));
        if (Directory.Exists(normalized)) normalized = InstanceRuntimePathResolver.Resolve(normalized);
        return Path.TrimEndingDirectorySeparator(normalized);
    }
}
