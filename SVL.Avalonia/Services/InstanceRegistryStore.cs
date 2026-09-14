using System.Text.Json;

namespace SVL.Avalonia.Services;

public sealed class InstanceRegistryStore
{
    // Modpack/Collection 安装任务可能并行完成。所有默认注册表实例共享同一
    // 进程级锁，避免“读-改-写”互相覆盖；文件本身仍由 AtomicFileWriter 原子替换。
    private static readonly object RegistryLock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _registryPath;

    public InstanceRegistryStore(string? storageDirectory = null)
    {
        var basePath = string.IsNullOrWhiteSpace(storageDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SVL",
                "Avalonia")
            : Path.GetFullPath(storageDirectory);

        Directory.CreateDirectory(basePath);
        _registryPath = Path.Combine(basePath, "instances-registry.json");
    }

    public bool Exists => File.Exists(_registryPath);

    public string GetRegistryPath() => _registryPath;

    public List<ManualInstanceRecord> LoadManualInstances()
    {
        lock (RegistryLock)
        {
            return LoadManualInstancesUnsafe();
        }
    }

    /// <summary>
    /// 在同一个临界区内新增或更新实例记录。
    /// 实例安装完成时必须使用此入口，不能在调用方分别 Load/Save，
    /// 否则并行安装会丢失另一条刚写入的记录。
    /// </summary>
    public void UpsertManualInstance(string name, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        lock (RegistryLock)
        {
            var records = LoadManualInstancesUnsafe();
            var existingIndex = records.FindIndex(record =>
                string.Equals(record.Path, path, StringComparison.OrdinalIgnoreCase));
            var record = new ManualInstanceRecord
            {
                Name = name ?? string.Empty,
                Path = path
            };

            if (existingIndex >= 0)
            {
                records[existingIndex] = record;
            }
            else
            {
                records.Add(record);
            }

            SaveManualInstancesUnsafe(records);
        }
    }

    /// <summary>仅当路径尚未登记时新增实例，返回是否实际新增。</summary>
    public bool TryAddManualInstance(string name, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        lock (RegistryLock)
        {
            var records = LoadManualInstancesUnsafe();
            if (records.Any(record =>
                    string.Equals(record.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            records.Add(new ManualInstanceRecord
            {
                Name = name ?? string.Empty,
                Path = path
            });
            SaveManualInstancesUnsafe(records);
            return true;
        }
    }

    /// <summary>原子重命名指定路径的所有记录，返回是否发生变化。</summary>
    public bool RenameManualInstancesByPath(string path, string name)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        lock (RegistryLock)
        {
            var records = LoadManualInstancesUnsafe();
            var changed = false;
            foreach (var record in records)
            {
                if (!string.Equals(record.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                record.Name = name ?? string.Empty;
                changed = true;
            }

            if (changed)
            {
                SaveManualInstancesUnsafe(records);
            }

            return changed;
        }
    }

    /// <summary>原子移除指定路径的记录，返回是否实际移除。</summary>
    public bool RemoveManualInstancesByPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        lock (RegistryLock)
        {
            var records = LoadManualInstancesUnsafe();
            var removed = records.RemoveAll(record =>
                string.Equals(record.Path, path, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
            {
                SaveManualInstancesUnsafe(records);
            }

            return removed;
        }
    }

    public void SaveManualInstances(IReadOnlyList<ManualInstanceRecord> records)
    {
        lock (RegistryLock)
        {
            SaveManualInstancesUnsafe(records);
        }
    }

    private List<ManualInstanceRecord> LoadManualInstancesUnsafe()
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

    private void SaveManualInstancesUnsafe(IReadOnlyList<ManualInstanceRecord> records)
    {
        var json = JsonSerializer.Serialize(records, JsonOptions);
        AtomicFileWriter.WriteUtf8(_registryPath, json);
    }
}

public sealed class ManualInstanceRecord
{
    public string Name { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;
}
