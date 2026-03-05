using System;
using SVL.Core.IO;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using ICSharpCode.SharpZipLib.Zip;
using SVL.Core.Logging;
using SVL.Core.Stardew.Mod;

namespace SVL.Core.Stardew.ResourceProject.Modpack;

public class ModpackManager
{
    public const string ManifestFileName = "modpack.json";
    public const string FileExtension = ".zip";

    public static async Task<bool> CreateModpackAsync(List<SdVMod> mods, string outputPath, string name, string description)
    {
        try
        {
            if (mods == null || mods.Count == 0)
            {
                Log.Warn("No mods selected for modpack");
                return false;
            }

            var manifest = new ModpackManifest
            {
                Name = name,
                Version = "1.0.0",
                Description = description,
                Author = "SVL User",
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Mods = mods.Select(m => new ModpackMod
                {
                    Id = m.Id,
                    UniqueId = m.UniqueId,
                    Enabled = m.IsEnabled,
                    Version = m.Version
                }).ToList()
            };

            var fileList = new List<string>();

            using (var archive = new ZipOutputStream(File.Create(outputPath + FileExtension)))
            {
                archive.SetLevel(9);

                var manifestJson = System.Text.Json.JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
                var manifestEntry = new ZipEntry(ManifestFileName)
                {
                    DateTime = DateTime.Now
                };
                archive.PutNextEntry(manifestEntry);
                var manifestBytes = System.Text.Encoding.UTF8.GetBytes(manifestJson);
                archive.Write(manifestBytes, 0, manifestBytes.Length);
                archive.CloseEntry();
                fileList.Add(ManifestFileName);

                foreach (var mod in mods.Where(m => m.IsEnabled && Directory.Exists(m.ModPath)))
                {
                    await AddModToArchiveAsync(archive, mod.ModPath, fileList);
                }

                var filesJson = System.Text.Json.JsonSerializer.Serialize(new ModpackFileList { Files = fileList });
                var fileListEntry = new ZipEntry("files.json")
                {
                    DateTime = DateTime.Now
                };
                archive.PutNextEntry(fileListEntry);
                var filesBytes = System.Text.Encoding.UTF8.GetBytes(filesJson);
                archive.Write(filesBytes, 0, filesBytes.Length);
                archive.CloseEntry();

                archive.Finish();
                archive.Close();

                Log.Info($"Created modpack: {name} with {mods.Count} mods");
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Failed to create modpack: {name}");
            return false;
        }
    }

    private static async Task AddModToArchiveAsync(ZipOutputStream archive, string modPath, List<string> fileList)
    {
        var modName = Path.GetFileName(modPath);
        var files = Directory.GetFiles(modPath, "*", SearchOption.AllDirectories);

        foreach (var file in files)
        {
            var entryName = Path.Combine(modName, file.Substring(modPath.Length + 1)).Replace("\\", "/");

            var entry = new ZipEntry(entryName)
            {
                DateTime = DateTime.Now,
                Size = new FileInfo(file).Length
            };
            archive.PutNextEntry(entry);

            using (var stream = File.OpenRead(file))
            {
                await stream.CopyToAsync(archive);
            }

            archive.CloseEntry();
            fileList.Add(entryName);
        }
    }

    public static async Task<ModpackManifest> LoadModpackAsync(string modpackPath)
    {
        try
        {
            var manifestPath = Path.Combine(modpackPath, ManifestFileName);

            if (!File.Exists(manifestPath))
            {
                Log.Error($"Modpack manifest not found: {manifestPath}");
                return null;
            }

            var json = await FileEx.ReadAllTextAsync(manifestPath);
            var manifest = System.Text.Json.JsonSerializer.Deserialize<ModpackManifest>(json);

            Log.Info($"Loaded modpack: {manifest.Name} with {manifest.Mods?.Count ?? 0} mods");
            return manifest;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Failed to load modpack: {modpackPath}");
            return null;
        }
    }

    public static async Task<bool> InstallModpackAsync(string modpackPath, string destinationModsPath)
    {
        try
        {
            var manifest = await LoadModpackAsync(modpackPath);

            if (manifest == null)
            {
                Log.Error("Invalid modpack");
                return false;
            }

            Log.Info($"Installing modpack: {manifest.Name}");

            using (var fs = new FileStream(modpackPath, FileMode.Open, FileAccess.Read))
            using (var archive = new ZipFile(fs))
            {
                foreach (ZipEntry entry in archive)
                {
                    if (entry.IsDirectory) continue;

                    var destinationPath = Path.Combine(destinationModsPath, entry.Name.Replace("/", "\\"));

                    var dirPath = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrEmpty(dirPath) && !Directory.Exists(dirPath))
                    {
                        Directory.CreateDirectory(dirPath);
                    }

                    using (var entryStream = archive.GetInputStream(entry))
                    using (var fsOut = File.Create(destinationPath))
                    {
                        await entryStream.CopyToAsync(fsOut);
                    }

                    Log.Info($"Extracted: {entry.Name}");
                }
            }

            var modManager = new SVL.Core.Stardew.Mod.ModManager();
            var installedMods = await modManager.LoadModsAsync(destinationModsPath);

            var enabledCount = installedMods.Count(m => m.IsEnabled);
            Log.Info($"Successfully installed {enabledCount} mods from modpack");

            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Failed to install modpack: {modpackPath}");
            return false;
        }
    }

    public static async Task<bool> ExportModpackAsync(List<SdVMod> mods, string outputPath, string name, string description)
    {
        return await CreateModpackAsync(mods, outputPath, name, description);
    }

    /// <summary>
    /// 增强导出：支持自定义元数据、打包 Mod 文件、导出 Mod 设置和 SVL 启动器
    /// </summary>
    /// <param name="mods">要导出的 Mod 列表</param>
    /// <param name="outputPath">输出路径（不含扩展名）</param>
    /// <param name="name">整合包名称</param>
    /// <param name="version">整合包版本</param>
    /// <param name="author">整合包作者</param>
    /// <param name="description">整合包描述</param>
    /// <param name="includeModSettings">是否导出 Mod 设置</param>
    /// <param name="includeSvlLauncher">是否包含 SVL 启动器</param>
    /// <param name="modpackIconPath">整合包图标路径（可选）</param>
    /// <param name="progressCallback">进度回调 (0-100, 消息)</param>
    /// <param name="smapiVersion">SMAPI 版本</param>
    /// <param name="gameVersion">游戏版本</param>
    public static async Task<bool> ExportModpackEnhancedAsync(
        List<SdVMod> mods,
        string outputPath,
        string name,
        string version,
        string author,
        string description,
        bool includeModSettings,
        bool includeSvlLauncher,
        string? modpackIconPath = null,
        Action<int, string> progressCallback = null,
        string smapiVersion = "",
        string gameVersion = "")
    {
        try
        {
            if (mods == null || mods.Count == 0)
            {
                Log.Warn("[Export] No mods selected for export");
                return false;
            }

            // 过滤掉 SMAPI 内置 Mod（ConsoleCommands、SaveBackup、ErrorHandler）
            var originalCount = mods.Count;
            mods = mods.Where(m => !IsSmapiBundledMod(m)).ToList();
            if (mods.Count < originalCount)
            {
                Log.Info($"[Export] 已过滤 {originalCount - mods.Count} 个 SMAPI 内置 Mod");
            }

            if (mods.Count == 0)
            {
                Log.Warn("[Export] 过滤后无 Mod 可导出");
                return false;
            }

            progressCallback?.Invoke(5, "正在构建清单...");

            // 合规策略：不导出 Mod 本体文件。
            // - 有来源凭证：仅导出来源信息。
            // - 无来源凭证：导出来源信息 + 配置文件（最低可迁移信息）。
            var exportRows = new List<(SdVMod Mod, object? Source, bool HasSourceCredential, bool ExportSettings)>();

            // 构建 manifest
            var manifest = new ModpackManifest
            {
                Name = name,
                Version = version ?? "1.0.0",
                Description = description ?? string.Empty,
                Author = author ?? string.Empty,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                SmapiVersion = smapiVersion ?? string.Empty,
                GameVersion = gameVersion ?? string.Empty,
                Mods = mods.Select(m => new ModpackMod
                {
                    Id = m.Id,
                    UniqueId = m.UniqueId,
                    Enabled = m.IsEnabled,
                    Version = m.Version
                }).ToList()
            };

            // 构建来源信息 JSON（优先使用 svl-source.json，回退到 SdVMod 属性和 manifest UpdateKeys）
            var sources = new List<object>();
            foreach (var mod in mods)
            {
                var sourceInfo = TryReadModSourceCredential(mod.ModPath);

                // 如果没有 svl-source.json，尝试从 SdVMod 属性合成来源信息
                if (sourceInfo == null)
                {
                    sourceInfo = SynthesizeSourceFromMod(mod);
                }

                var hasSourceCredential = sourceInfo != null;
                var exportSettings = Directory.Exists(mod.ModPath) && (includeModSettings || !hasSourceCredential);

                exportRows.Add((mod, sourceInfo, hasSourceCredential, exportSettings));

                sources.Add(new
                {
                    uniqueId = mod.UniqueId ?? string.Empty,
                    name = mod.Name ?? string.Empty,
                    version = mod.Version ?? string.Empty,
                    enabled = mod.IsEnabled,
                    bundled = false,
                    source = sourceInfo
                });
            }
            var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            var sourcesJson = JsonSerializer.Serialize(sources, new JsonSerializerOptions { WriteIndented = true });

            if (includeSvlLauncher)
            {
                // 涵盖启动器：外层 zip = { SVL.exe, modpack.zip }
                // 先将整合包内容写入内存中的嵌套 modpack.zip
                progressCallback?.Invoke(10, "正在构建整合包数据...");

                byte[] innerZipBytes;
                using (var innerStream = new MemoryStream())
                {
                    using (var innerArchive = new ZipOutputStream(innerStream))
                    {
                        innerArchive.IsStreamOwner = false;
                        innerArchive.SetLevel(9);

                        var innerFileList = new List<string>();

                        await WriteZipEntryAsync(innerArchive, ManifestFileName, manifestJson, innerFileList);
                        await WriteZipEntryAsync(innerArchive, "sources.json", sourcesJson, innerFileList);

                        await AddModpackIconToArchiveAsync(innerArchive, modpackIconPath, innerFileList);

                        var settingsRows = exportRows.Where(x => x.ExportSettings).ToList();
                        if (settingsRows.Count > 0)
                        {
                            progressCallback?.Invoke(40, "正在导出 Mod 配置...");
                            var processedCount = 0;
                            foreach (var row in settingsRows)
                            {
                                processedCount++;
                                var modName = Path.GetFileName(row.Mod.ModPath);
                                var percent = 40 + (int)(processedCount * 30.0 / Math.Max(settingsRows.Count, 1));
                                progressCallback?.Invoke(Math.Min(percent, 70), $"正在导出配置: {modName}");
                                await AddModSettingsToArchiveAsync(innerArchive, row.Mod.ModPath, innerFileList);
                            }
                        }

                        var filesJson = JsonSerializer.Serialize(new ModpackFileList { Files = innerFileList });
                        await WriteZipEntryAsync(innerArchive, "files.json", filesJson, innerFileList);

                        innerArchive.Finish();
                    }
                    innerZipBytes = innerStream.ToArray();
                }

                // 写入外层 zip: SVL.exe + modpack.zip
                progressCallback?.Invoke(80, "正在打包 SVL 启动器...");
                var outerFileList = new List<string>();
                using (var outerArchive = new ZipOutputStream(File.Create(outputPath + FileExtension)))
                {
                    outerArchive.SetLevel(9);

                    // 添加 SVL.exe
                    await AddSvlExeToArchiveAsync(outerArchive, outerFileList);

                    // 添加嵌套的 modpack.zip
                    progressCallback?.Invoke(90, "正在写入整合包数据...");
                    var modpackEntry = new ZipEntry("modpack.zip")
                    {
                        DateTime = DateTime.Now,
                        Size = innerZipBytes.Length
                    };
                    outerArchive.PutNextEntry(modpackEntry);
                    await outerArchive.WriteAsync(innerZipBytes, 0, innerZipBytes.Length);
                    outerArchive.CloseEntry();

                    outerArchive.Finish();
                    outerArchive.Close();
                }

                progressCallback?.Invoke(95, "正在完成...");
                Log.Info($"[Export] Created bundled modpack with launcher: {name} v{version} with {mods.Count} mods");
            }
            else
            {
                // 不含启动器：直接平铺写入
                var fileList = new List<string>();

                using (var archive = new ZipOutputStream(File.Create(outputPath + FileExtension)))
                {
                    archive.SetLevel(9);

                    await WriteZipEntryAsync(archive, ManifestFileName, manifestJson, fileList);

                    progressCallback?.Invoke(10, "正在收集来源信息...");
                    await WriteZipEntryAsync(archive, "sources.json", sourcesJson, fileList);

                    await AddModpackIconToArchiveAsync(archive, modpackIconPath, fileList);

                    var settingsRows = exportRows.Where(x => x.ExportSettings).ToList();
                    if (settingsRows.Count > 0)
                    {
                        progressCallback?.Invoke(40, "正在导出 Mod 配置...");
                        var processedCount = 0;
                        foreach (var row in settingsRows)
                        {
                            processedCount++;
                            var modName = Path.GetFileName(row.Mod.ModPath);
                            var percent = 40 + (int)(processedCount * 35.0 / Math.Max(settingsRows.Count, 1));
                            progressCallback?.Invoke(Math.Min(percent, 75), $"正在导出配置: {modName}");
                            await AddModSettingsToArchiveAsync(archive, row.Mod.ModPath, fileList);
                        }
                    }

                    progressCallback?.Invoke(90, "正在写入文件索引...");
                    var filesJson = JsonSerializer.Serialize(new ModpackFileList { Files = fileList });
                    await WriteZipEntryAsync(archive, "files.json", filesJson, fileList);

                    archive.Finish();
                    archive.Close();

                    progressCallback?.Invoke(95, "正在完成...");
                    Log.Info($"[Export] Created enhanced modpack: {name} v{version} with {mods.Count} mods (bundled=false)");
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"[Export] Failed to create enhanced modpack: {name}");
            return false;
        }
    }

    /// <summary>
    /// 写入 ZIP 条目（文本内容）
    /// </summary>
    private static async Task WriteZipEntryAsync(ZipOutputStream archive, string entryName, string content, List<string> fileList)
    {
        var entry = new ZipEntry(entryName) { DateTime = DateTime.Now };
        archive.PutNextEntry(entry);
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        await archive.WriteAsync(bytes, 0, bytes.Length);
        archive.CloseEntry();
        fileList.Add(entryName);
    }

    /// <summary>
    /// 添加 Mod 文件到 ZIP（可选是否包含设置文件）
    /// </summary>
    private static async Task AddModToArchiveAsync(ZipOutputStream archive, string modPath, List<string> fileList, bool includeSettings)
    {
        var modName = Path.GetFileName(modPath);
        var files = Directory.GetFiles(modPath, "*", SearchOption.AllDirectories);

        foreach (var file in files)
        {
            var relativePath = file.Substring(modPath.Length + 1);
            var fileName = Path.GetFileName(file).ToLowerInvariant();

            // 如果不包含设置，跳过配置文件
            if (!includeSettings && IsSettingsFile(fileName))
                continue;

            var entryName = Path.Combine("mods", modName, relativePath).Replace("\\", "/");
            var entry = new ZipEntry(entryName)
            {
                DateTime = DateTime.Now,
                Size = new FileInfo(file).Length
            };
            archive.PutNextEntry(entry);

            using (var stream = File.OpenRead(file))
            {
                await stream.CopyToAsync(archive);
            }

            archive.CloseEntry();
            fileList.Add(entryName);
        }
    }

    /// <summary>
    /// 仅添加 Mod 的设置文件（config.json 等）
    /// </summary>
    private static async Task AddModSettingsToArchiveAsync(ZipOutputStream archive, string modPath, List<string> fileList)
    {
        var modName = Path.GetFileName(modPath);
        var files = Directory.GetFiles(modPath, "*", SearchOption.AllDirectories);

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file).ToLowerInvariant();
            if (!IsSettingsFile(fileName))
                continue;

            var relativePath = file.Substring(modPath.Length + 1);
            var entryName = Path.Combine("settings", modName, relativePath).Replace("\\", "/");

            var entry = new ZipEntry(entryName)
            {
                DateTime = DateTime.Now,
                Size = new FileInfo(file).Length
            };
            archive.PutNextEntry(entry);

            using (var stream = File.OpenRead(file))
            {
                await stream.CopyToAsync(archive);
            }

            archive.CloseEntry();
            fileList.Add(entryName);
        }
    }

    /// <summary>
    /// 将整合包图标写入 ZIP 根目录（modpack-icon.*）
    /// </summary>
    private static async Task AddModpackIconToArchiveAsync(ZipOutputStream archive, string? iconPath, List<string> fileList)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(iconPath) || !Path.IsPathRooted(iconPath) || !File.Exists(iconPath))
                return;

            var ext = Path.GetExtension(iconPath)?.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(ext))
                ext = ".png";

            var entryName = $"modpack-icon{ext}";
            var entry = new ZipEntry(entryName)
            {
                DateTime = DateTime.Now,
                Size = new FileInfo(iconPath).Length
            };

            archive.PutNextEntry(entry);
            using (var stream = File.OpenRead(iconPath))
            {
                await stream.CopyToAsync(archive);
            }
            archive.CloseEntry();

            fileList.Add(entryName);
            Log.Info($"[Export] 已写入整合包图标: {entryName}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[Export] 写入整合包图标失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 判断是否为 Mod 设置/配置文件
    /// </summary>
    private static bool IsSettingsFile(string fileName)
    {
        return fileName == "config.json"
            || fileName == "config.yaml"
            || fileName == "config.yml"
            || fileName == "settings.json"
            || fileName.EndsWith(".config.json");
    }

    /// <summary>
    /// SMAPI 内置 Mod 标识，不应纳入导出
    /// </summary>
    private static readonly HashSet<string> SmapiBundledMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "SMAPI.ConsoleCommands", "ConsoleCommands",
        "SMAPI.SaveBackup", "SaveBackup",
        "SMAPI.ErrorHandler", "ErrorHandler",
        "SMAPI Installer", "SMAPIInstaller"
    };

    /// <summary>
    /// 判断 Mod 是否为 SMAPI 内置 Mod
    /// </summary>
    private static bool IsSmapiBundledMod(SdVMod mod)
    {
        if (mod == null) return false;
        if (!string.IsNullOrEmpty(mod.UniqueId) && SmapiBundledMods.Contains(mod.UniqueId))
            return true;
        var folderName = Path.GetFileName(mod.ModPath ?? string.Empty);
        if (!string.IsNullOrEmpty(folderName) && SmapiBundledMods.Contains(folderName))
            return true;
        // 匹配 "SMAPI x.y.z installer" 格式的文件夹名
        if (!string.IsNullOrEmpty(folderName) && folderName.StartsWith("SMAPI ", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    /// <summary>
    /// 获取 SVL 启动器可执行文件路径
    /// </summary>
    private static string GetSvlExePath()
    {
        var exePath = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            return null;
        return exePath;
    }

    /// <summary>
    /// 将 SVL.exe 添加到 ZIP 根目录
    /// </summary>
    private static async Task AddSvlExeToArchiveAsync(ZipOutputStream archive, List<string> fileList)
    {
        var exePath = GetSvlExePath();
        if (exePath == null)
        {
            Log.Warn("[Export] 无法定位 SVL 启动器程序");
            return;
        }

        var fileName = Path.GetFileName(exePath);
        var entry = new ZipEntry(fileName)
        {
            DateTime = DateTime.Now,
            Size = new FileInfo(exePath).Length
        };
        archive.PutNextEntry(entry);

        using (var stream = File.OpenRead(exePath))
        {
            await stream.CopyToAsync(archive);
        }

        archive.CloseEntry();
        fileList.Add(fileName);
        Log.Info($"[Export] SVL 启动器已打包: {fileName}");
    }

    /// <summary>
    /// 读取 Mod 目录下的来源凭证（svl-source.json 或 .source.json）
    /// </summary>
    private static object TryReadModSourceCredential(string modPath)
    {
        try
        {
            if (string.IsNullOrEmpty(modPath) || !Directory.Exists(modPath))
                return null;

            // 优先读取 svl-source.json
            var svlSourcePath = Path.Combine(modPath, "svl-source.json");
            if (File.Exists(svlSourcePath))
            {
                var json = File.ReadAllText(svlSourcePath);
                return JsonSerializer.Deserialize<JsonElement>(json);
            }

            // 回退到 .source.json
            var dotSourcePath = Path.Combine(modPath, ".source.json");
            if (File.Exists(dotSourcePath))
            {
                var json = File.ReadAllText(dotSourcePath);
                return JsonSerializer.Deserialize<JsonElement>(json);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 从 SdVMod 属性和 manifest UpdateKeys 合成来源信息
    /// 当 svl-source.json 不存在时作为回退
    /// </summary>
    private static object SynthesizeSourceFromMod(SdVMod mod)
    {
        try
        {
            string platform = null;
            string projectId = null;
            string downloadUrl = null;

            // 1. 优先使用已有的平台属性
            if (!string.IsNullOrEmpty(mod.CurseforgeProjectId))
            {
                platform = "Curseforge";
                projectId = mod.CurseforgeProjectId;
            }
            else if (!string.IsNullOrEmpty(mod.NexusModsProjectId))
            {
                platform = "NexusMods";
                projectId = mod.NexusModsProjectId;
            }

            // 2. 从 manifest UpdateKeys 提取平台和项目 ID
            if (platform == null && mod.Manifest?.UpdateKeys != null)
            {
                foreach (var key in mod.Manifest.UpdateKeys)
                {
                    if (string.IsNullOrEmpty(key)) continue;
                    var parts = key.Split(':');
                    if (parts.Length < 2) continue;

                    var keyPlatform = parts[0].Trim();
                    var keyId = parts[1].Trim();

                    if (string.Equals(keyPlatform, "Nexus", StringComparison.OrdinalIgnoreCase))
                    {
                        platform = "NexusMods";
                        projectId = keyId;
                        break; // 优先 Nexus
                    }
                    else if (string.Equals(keyPlatform, "CurseForge", StringComparison.OrdinalIgnoreCase))
                    {
                        platform ??= "Curseforge";
                        projectId ??= keyId;
                    }
                }
            }

            // 3. 使用 UpdateUrl（如果有）
            if (!string.IsNullOrEmpty(mod.UpdateUrl))
            {
                downloadUrl = mod.UpdateUrl;
            }

            if (platform == null && string.IsNullOrEmpty(downloadUrl))
                return null;

            return new
            {
                platform = platform ?? string.Empty,
                projectId = projectId ?? string.Empty,
                fileId = string.Empty,
                modId = mod.Id ?? string.Empty,
                modName = mod.Name ?? string.Empty,
                fileName = string.Empty,
                downloadUrl = downloadUrl ?? string.Empty,
                synthesized = true,  // 标记为合成的来源信息
                schemaVersion = 1
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"[ModpackManager] 合成来源信息失败: {mod.Name} - {ex.Message}");
            return null;
        }
    }
}
