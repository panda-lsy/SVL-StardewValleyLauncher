using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading.Tasks;
using SVL.Core.Download;
using SVL.Core.IO;
using SVL.Core.Logging;
using SVL.Core.Stardew.ResourceProject.NexusMods;

namespace SVL.Core.Stardew.Mod;

public class ModManager : IModManager
{
    private List<SdVMod> _loadedMods = [];

    public List<SdVMod> LoadedMods => _loadedMods;

    public async Task<List<SdVMod>> LoadModsAsync(string modsPath)
    {
        _loadedMods.Clear();

        if (!Directory.Exists(modsPath))
        {
            Directory.CreateDirectory(modsPath);
            return _loadedMods;
        }

        var manifestFiles = Directory.GetFiles(modsPath, "manifest.json", SearchOption.AllDirectories);
        var discoveredMods = new List<SdVMod>();

        foreach (var manifestFile in manifestFiles)
        {
            try
            {
                var loadedMod = await LoadModFromManifestAsync(modsPath, manifestFile);
                if (loadedMod != null)
                {
                    discoveredMods.Add(loadedMod);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Failed to load mod: {manifestFile}");
            }
        }

        BuildCompositeHierarchy(modsPath, discoveredMods);

        _loadedMods.AddRange(discoveredMods
            .OrderBy(mod => mod.IsChildMod ? 1 : 0)
            .ThenBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase));

        Log.Info($"Loaded {_loadedMods.Count} mods from {modsPath}");
        return _loadedMods;
    }

    private async Task<SdVMod?> LoadModFromManifestAsync(string modsPath, string manifestFile)
    {
        var modDir = Path.GetDirectoryName(manifestFile);
        if (string.IsNullOrWhiteSpace(modDir))
            return null;

        var relative = GetRelativePathPortable(modsPath, modDir);
        var relativeParts = relative
            .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

        if (relativeParts.Length == 0)
            return null;

        var manifestJson = await FileEx.ReadAllTextAsync(manifestFile);
        var options = new JsonSerializerOptions
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            PropertyNameCaseInsensitive = true
        };

        var manifest = JsonSerializer.Deserialize<SdVModManifest>(manifestJson, options);
        if (manifest == null)
            return null;

        var sdvMod = new SdVMod
        {
            Id = relative.Replace(Path.DirectorySeparatorChar, '/'),
            Name = manifest.Name,
            Author = manifest.Author,
            Version = manifest.Version,
            Description = manifest.Description,
            UniqueId = manifest.UniqueId,
            ModPath = modDir,
            IsEnabled = !relativeParts.Any(part => part.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)),
            IsContentPack = manifest.ContentPackFor != null,
            InstalledDate = Directory.GetCreationTime(modDir),
            Manifest = manifest,
            Dependencies = manifest.Dependencies?.Select(d => d.UniqueId).ToList() ?? [],
            Thumbnail = Path.Combine(modDir, "icon.png"),
            Tags = relativeParts.Length > 1
                ? relativeParts.Take(relativeParts.Length - 1).Select(NormalizeFolderSegment).ToList()
                : []
        };

        ApplySourceCredential(sdvMod, TryLoadSourceCredential(modDir));

        if (!File.Exists(sdvMod.Thumbnail))
        {
            sdvMod.Thumbnail = null;
        }

        return sdvMod;
    }

    private void BuildCompositeHierarchy(string modsPath, List<SdVMod> discoveredMods)
    {
        if (discoveredMods.Count == 0)
            return;

        var modsByPath = discoveredMods
            .Where(mod => !string.IsNullOrWhiteSpace(mod.ModPath))
            .GroupBy(mod => NormalizePathKey(mod.ModPath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var sourceCredentialsByDir = Directory.GetFiles(modsPath, "svl-source.json", SearchOption.AllDirectories)
            .Select(path => Path.GetDirectoryName(path))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(dir => new { Dir = dir!, Credential = TryLoadSourceCredential(dir!) })
            .Where(item => item.Credential != null)
            .ToDictionary(item => NormalizePathKey(item.Dir), item => item.Credential!, StringComparer.OrdinalIgnoreCase);

        foreach (var mod in discoveredMods)
        {
            mod.ChildMods.Clear();
            mod.IsChildMod = false;
            mod.IsCompositeParent = false;
            mod.ParentModId = string.Empty;
            mod.ParentModName = string.Empty;
        }

        foreach (var entry in sourceCredentialsByDir)
        {
            var childPathKey = entry.Key;
            var credential = entry.Value;
            if (credential.ParentMod == null || string.IsNullOrWhiteSpace(credential.ParentMod.RelativePath))
                continue;

            if (!modsByPath.TryGetValue(childPathKey, out var child))
                continue;

            var parentPath = ResolveCredentialPath(modsPath, credential.ParentMod.RelativePath);
            var parent = GetOrCreateCompositeParentMod(modsPath, parentPath, discoveredMods, modsByPath, sourceCredentialsByDir, child);
            AttachChildToParent(parent, child);
        }

        foreach (var entry in sourceCredentialsByDir)
        {
            var credential = entry.Value;
            if (!credential.IsParentMod)
                continue;

            var parentPath = entry.Key;
            var parent = GetOrCreateCompositeParentMod(modsPath, parentPath, discoveredMods, modsByPath, sourceCredentialsByDir, null);

            foreach (var childEntry in credential.ChildMods ?? [])
            {
                if (string.IsNullOrWhiteSpace(childEntry.RelativePath))
                    continue;

                var childPath = ResolveCredentialPath(modsPath, childEntry.RelativePath);
                if (!modsByPath.TryGetValue(NormalizePathKey(childPath), out var child))
                    continue;

                AttachChildToParent(parent, child);
            }
        }

        foreach (var parent in discoveredMods.Where(mod => mod.IsCompositeParent))
        {
            var orderedChildren = parent.ChildMods
                .OrderBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            parent.ChildMods.Clear();
            foreach (var child in orderedChildren)
            {
                parent.ChildMods.Add(child);
            }

            PromoteCompositeSourceCredential(parent, orderedChildren, parent.ModPath);

            if (string.IsNullOrWhiteSpace(parent.Description))
            {
                parent.Description = $"包含 {orderedChildren.Count} 个子 Mod";
            }

            if (string.IsNullOrWhiteSpace(parent.Author))
            {
                parent.Author = BuildCompositeAuthor(orderedChildren);
            }

            if (string.IsNullOrWhiteSpace(parent.Version))
            {
                parent.Version = BuildCompositeVersion(orderedChildren);
            }
        }
    }

    private SdVMod GetOrCreateCompositeParentMod(
        string modsPath,
        string parentPath,
        List<SdVMod> discoveredMods,
        IDictionary<string, SdVMod> modsByPath,
        IReadOnlyDictionary<string, SvlSourceMetadata> sourceCredentialsByDir,
        SdVMod? fallbackChild)
    {
        var parentPathKey = NormalizePathKey(parentPath);
        if (modsByPath.TryGetValue(parentPathKey, out var existingParent))
        {
            existingParent.IsCompositeParent = true;
            if (sourceCredentialsByDir.TryGetValue(parentPathKey, out var existingCredential))
            {
                ApplySourceCredential(existingParent, existingCredential);
            }

            return existingParent;
        }

        sourceCredentialsByDir.TryGetValue(parentPathKey, out var credential);
        var children = discoveredMods
            .Where(mod => IsDescendantOf(mod.ModPath, parentPath))
            .OrderByDescending(item => item.InstalledDate)
            .ToList();

        if (fallbackChild != null && children.All(child => !ReferenceEquals(child, fallbackChild)))
        {
            children.Add(fallbackChild);
        }

        var newestInstalled = children.OrderByDescending(item => item.InstalledDate).FirstOrDefault() ?? fallbackChild;
        var parent = new SdVMod
        {
            Id = GetRelativePathPortable(modsPath, parentPath).Replace(Path.DirectorySeparatorChar, '/'),
            Name = !string.IsNullOrWhiteSpace(credential?.ModName)
                ? credential.ModName
                : NormalizeFolderSegment(Path.GetFileName(parentPath)),
            Author = BuildCompositeAuthor(children),
            Version = BuildCompositeVersion(children),
            Description = children.Count > 0 ? $"包含 {children.Count} 个子 Mod" : string.Empty,
            UniqueId = string.Empty,
            ModPath = parentPath,
            IsEnabled = !Path.GetFileName(parentPath).EndsWith(".disabled", StringComparison.OrdinalIgnoreCase),
            IsContentPack = false,
            InstalledDate = newestInstalled?.InstalledDate ?? (Directory.Exists(parentPath) ? Directory.GetCreationTime(parentPath) : DateTime.Now),
            Manifest = null,
            Thumbnail = children.Select(item => item.Thumbnail).FirstOrDefault(path => !string.IsNullOrWhiteSpace(path)),
            IsCompositeParent = true
        };

        ApplySourceCredential(parent, credential);

        discoveredMods.Add(parent);
        modsByPath[parentPathKey] = parent;
        return parent;
    }

    private static void AttachChildToParent(SdVMod parent, SdVMod child)
    {
        if (parent == null || child == null || ReferenceEquals(parent, child))
            return;

        parent.IsCompositeParent = true;
        child.IsChildMod = true;
        child.ParentModId = parent.Id;
        child.ParentModName = parent.Name;

        if (!parent.ChildMods.Any(existing => ReferenceEquals(existing, child) || PathsEqual(existing.ModPath, child.ModPath)))
        {
            parent.ChildMods.Add(child);
        }
    }

    private static bool IsDescendantOf(string? candidatePath, string? parentPath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(parentPath))
            return false;

        var parent = NormalizePathKey(parentPath);
        var candidate = NormalizePathKey(candidatePath);
        if (string.Equals(parent, candidate, StringComparison.OrdinalIgnoreCase))
            return false;

        return candidate.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(parent + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveCredentialPath(string modsPath, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            return relativePath;

        var normalizedRelative = relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return Path.Combine(modsPath, normalizedRelative);
    }

    private static string NormalizePathKey(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        var normalizedLeft = left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRight = right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeFolderSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return value.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
            ? value.Substring(0, value.Length - ".disabled".Length)
            : value;
    }

    private static string BuildCompositeAuthor(IEnumerable<SdVMod> children)
    {
        var authors = children
            .Select(item => item.Author)
            .Where(author => !string.IsNullOrWhiteSpace(author))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (authors.Count == 0)
            return string.Empty;

        if (authors.Count == 1)
            return authors[0];

        return $"{authors.Count} 位作者";
    }

    private static string BuildCompositeVersion(IEnumerable<SdVMod> children)
    {
        return children
            .Select(item => item.Version)
            .FirstOrDefault(version => !string.IsNullOrWhiteSpace(version))
            ?? string.Empty;
    }

    private void PromoteCompositeSourceCredential(SdVMod parent, IReadOnlyCollection<SdVMod> children, string rootPath)
    {
        if (!string.IsNullOrWhiteSpace(parent.CurseforgeProjectId) || !string.IsNullOrWhiteSpace(parent.NexusModsProjectId))
            return;

        var childCredentials = children
            .Select(child => new { Child = child, Credential = TryLoadSourceCredential(child.ModPath) })
            .Where(item => item.Credential != null && !string.IsNullOrWhiteSpace(item.Credential.ProjectId))
            .ToList();

        if (childCredentials.Count == 0)
            return;

        var grouped = childCredentials
            .GroupBy(item => $"{NormalizePlatform(item.Credential!.Platform)}|{NormalizeId(item.Credential.ProjectId)}", StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .FirstOrDefault();

        if (grouped == null)
            return;

        if (grouped.Count() != childCredentials.Count)
            return;

        var candidate = grouped.First().Credential;
        if (candidate == null || string.IsNullOrWhiteSpace(candidate.ProjectId))
            return;

        ApplySourceCredential(parent, candidate);

        if (string.IsNullOrWhiteSpace(parent.Version) && !string.IsNullOrWhiteSpace(candidate.FileName))
        {
            parent.Version = ExtractVersionFromText(candidate.FileName);
        }

        if (string.IsNullOrWhiteSpace(parent.Name) && !string.IsNullOrWhiteSpace(candidate.ModName))
        {
            parent.Name = candidate.ModName;
        }

        if (!string.IsNullOrWhiteSpace(parent.ModPath) && !Directory.Exists(parent.ModPath) && Directory.Exists(rootPath))
        {
            parent.ModPath = rootPath;
        }
    }

    private void ApplySourceCredential(SdVMod mod, SvlSourceMetadata? sourceCredential)
    {
        if (mod == null || sourceCredential == null)
            return;

        if (string.Equals(sourceCredential.Platform, "Curseforge", StringComparison.OrdinalIgnoreCase))
        {
            var curseId = ExtractIntId(sourceCredential.ProjectId);
            mod.CurseforgeProjectId = curseId > 0 ? curseId.ToString() : (sourceCredential.ProjectId ?? string.Empty);
            mod.UpdateSource = "Curseforge";
        }
        else if (string.Equals(sourceCredential.Platform, "NexusMods", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(sourceCredential.Platform, "Nexus", StringComparison.OrdinalIgnoreCase))
        {
            var nexusId = ExtractLongId(sourceCredential.ProjectId);
            mod.NexusModsProjectId = nexusId > 0 ? nexusId.ToString() : (sourceCredential.ProjectId ?? string.Empty);
            mod.UpdateSource = "NexusMods";
        }

        if (string.IsNullOrWhiteSpace(mod.SourceFileName) && !string.IsNullOrWhiteSpace(sourceCredential.FileName))
        {
            mod.SourceFileName = sourceCredential.FileName;
        }

        if (string.IsNullOrWhiteSpace(mod.Version) && !string.IsNullOrWhiteSpace(sourceCredential.FileName))
        {
            mod.Version = ExtractVersionFromText(sourceCredential.FileName);
        }

        if (string.IsNullOrWhiteSpace(mod.Name) && !string.IsNullOrWhiteSpace(sourceCredential.ModName))
        {
            mod.Name = sourceCredential.ModName;
        }

        mod.ApplyLocalization(sourceCredential.Localization);
    }

    private static string GetRelativePathPortable(string basePath, string fullPath)
    {
        if (string.IsNullOrWhiteSpace(basePath) || string.IsNullOrWhiteSpace(fullPath))
            return fullPath;

        var normalizedBase = basePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedFull = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (normalizedFull.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase))
        {
            var relative = normalizedFull.Substring(normalizedBase.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.IsNullOrWhiteSpace(relative) ? Path.GetFileName(normalizedFull) : relative;
        }

        return fullPath;
    }

    public async Task<bool> InstallModAsync(string modPath, string destinationModsPath)
    {
        try
        {
            if (!File.Exists(modPath) && !Directory.Exists(modPath))
            {
                Log.Error($"Mod path does not exist: {modPath}");
                return false;
            }

            var modName = Path.GetFileName(modPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? modPath.Substring(0, modPath.Length - 4)
                : modPath);
            var destPath = Path.Combine(destinationModsPath, modName);

            if (Directory.Exists(destPath))
            {
                ModBackupService.BackupDirectory(destinationModsPath, destPath);
                MovePathToRecycleBin(destPath);
            }

            if (modPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                await FileServiceExtensions.ExtractModAsync(modPath, destPath);
            }
            else if (Directory.Exists(modPath))
            {
                await FileServiceExtensions.CopyDirectoryAsync(modPath, destPath, true);
            }

            Log.Info($"Successfully installed mod: {modName}");
            await ValidateManifestAsync(destPath);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Failed to install mod: {modPath}");
            return false;
        }
    }

    public async Task<bool> UninstallModAsync(string modId, string modsPath)
    {
        try
        {
            var mod = _loadedMods.FirstOrDefault(m => m.Id == modId || m.UniqueId == modId);
            if (mod == null)
            {
                Log.Error($"Mod not found: {modId}");
                return false;
            }

            if (!string.IsNullOrWhiteSpace(mod.ModPath) && (Directory.Exists(mod.ModPath) || File.Exists(mod.ModPath)))
            {
                var moved = await Task.Run(() => MovePathToRecycleBin(mod.ModPath));
                if (!moved)
                {
                    Log.Error($"[ModManager] 移动到回收站失败: {mod.ModPath}");
                    return false;
                }
            }
            else
            {
                Log.Warn($"[ModManager] 卸载目标不存在，跳过删除: {mod.ModPath}");
            }

            _loadedMods.Remove(mod);

            Log.Info($"Successfully uninstalled mod to recycle bin: {mod.Name}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Failed to uninstall mod: {modId}");
            return false;
        }
    }

    public async Task<bool> EnableModAsync(string modId, string modsPath)
    {
        try
        {
            var mod = _loadedMods.FirstOrDefault(m => m.Id == modId || m.UniqueId == modId);
            if (mod == null)
            {
                Log.Error($"Mod not found: {modId}");
                return false;
            }

            var currentPath = mod.ModPath;
            if (mod.IsEnabled)
            {
                Log.Warn($"Mod already enabled: {mod.Name}");
                return true;
            }

            var newPath = currentPath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                ? currentPath.Substring(0, currentPath.Length - 9)
                : $"{currentPath}.disabled";

            await FileServiceExtensions.MoveAsync(currentPath, newPath);

            mod.IsEnabled = true;
            mod.ModPath = newPath;

            Log.Info($"Enabled mod: {mod.Name}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Failed to enable mod: {modId}");
            return false;
        }
    }

    public async Task<bool> DisableModAsync(string modId, string modsPath)
    {
        try
        {
            var mod = _loadedMods.FirstOrDefault(m => m.Id == modId || m.UniqueId == modId);
            if (mod == null)
            {
                Log.Error($"Mod not found: {modId}");
                return false;
            }

            if (!mod.IsEnabled)
            {
                Log.Warn($"Mod already disabled: {mod.Name}");
                return true;
            }

            var currentPath = mod.ModPath;
            var newPath = $"{currentPath}.disabled";

            await FileServiceExtensions.MoveAsync(currentPath, newPath);

            mod.IsEnabled = false;
            mod.ModPath = newPath;

            Log.Info($"Disabled mod: {mod.Name}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Failed to disable mod: {modId}");
            return false;
        }
    }

    public async Task<bool> ValidateManifestAsync(string modPath)
    {
        var manifestPath = Path.Combine(modPath, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            Log.Warn($"No manifest found: {modPath}");
            return false;
        }

        var json = await FileEx.ReadAllTextAsync(manifestPath);
        var manifest = System.Text.Json.JsonSerializer.Deserialize<SdVModManifest>(json);

        if (string.IsNullOrEmpty(manifest?.Name) || string.IsNullOrEmpty(manifest?.UniqueId))
        {
            Log.Error($"Invalid manifest: {modPath}");
            return false;
        }

        Log.Info($"Validated manifest: {manifest.Name} ({manifest.UniqueId})");
        return true;
    }

    /// <summary>
    /// 检查MOD更新（Curseforge > NexusMods 优先级）
    /// </summary>
    public async Task CheckModUpdatesAsync(List<SdVMod> mods)
    {
        Log.Info($"[CheckModUpdates] 开始检查 {mods.Count} 个 MOD 的更新");

        bool tokenExpiredShown = false; // 标记是否已显示Token过期通知

        foreach (var mod in mods)
        {
            Log.Info($"[CheckModUpdates] 检查 MOD: {mod.Name} (UniqueId: {mod.UniqueId})");

            if (mod.IsChildMod)
            {
                Log.Info($"[CheckModUpdates] MOD {mod.Name} 是子 Mod，跳过独立更新检查");
                mod.HasUpdate = false;
                mod.UpdateSource = string.IsNullOrWhiteSpace(mod.UpdateSource) ? "ParentManaged" : mod.UpdateSource;
                continue;
            }

            // 优先使用来源凭证（svl-source）恢复出的平台和项目ID
            if (string.Equals(mod.UpdateSource, "Curseforge", StringComparison.OrdinalIgnoreCase)
                && ExtractIntId(mod.CurseforgeProjectId) > 0)
            {
                mod.IsCheckingUpdate = true;
                try
                {
                    if (!await TryCheckCurseforgeUpdateAsync(mod, mod.CurseforgeProjectId))
                        mod.HasUpdate = false;
                }
                finally
                {
                    mod.IsCheckingUpdate = false;
                }
                continue;
            }

            // 检查是否有manifest
            if (mod.Manifest == null)
            {
                Log.Warn($"[CheckModUpdates] MOD {mod.Name} 没有 manifest，且无可用来源凭证，跳过");
                mod.HasUpdate = false;
                mod.UpdateSource = string.IsNullOrWhiteSpace(mod.UpdateSource) ? "NoManifest" : mod.UpdateSource;
                continue;
            }

            if (string.Equals(mod.UpdateSource, "NexusMods", StringComparison.OrdinalIgnoreCase)
                && ExtractLongId(mod.NexusModsProjectId) > 0)
            {
                mod.IsCheckingUpdate = true;
                try
                {
                    if (!await TryCheckNexusUpdateAsync(mod, mod.NexusModsProjectId))
                        mod.HasUpdate = false;
                }
                finally
                {
                    mod.IsCheckingUpdate = false;
                }
                continue;
            }

            // 检查是否有UpdateKeys
            if (mod.Manifest.UpdateKeys == null || mod.Manifest.UpdateKeys.Count == 0)
            {
                // 无 UpdateKeys 时尝试使用来源凭证（Curseforge/Nexus）
                if (!string.IsNullOrWhiteSpace(mod.CurseforgeProjectId))
                {
                    Log.Info($"[CheckModUpdates] MOD {mod.Name} 无 UpdateKeys，使用 Curseforge 来源凭证: {mod.CurseforgeProjectId}");
                    mod.UpdateSource = "Curseforge";
                    mod.IsCheckingUpdate = true;
                    try
                    {
                        if (!await TryCheckCurseforgeUpdateAsync(mod, mod.CurseforgeProjectId))
                            mod.HasUpdate = false;
                    }
                    finally
                    {
                        mod.IsCheckingUpdate = false;
                    }
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(mod.NexusModsProjectId))
                {
                    Log.Info($"[CheckModUpdates] MOD {mod.Name} 无 UpdateKeys，使用 Nexus 来源凭证: {mod.NexusModsProjectId}");
                    mod.UpdateSource = "NexusMods";
                    mod.IsCheckingUpdate = true;
                    try
                    {
                        if (!await TryCheckNexusUpdateAsync(mod, mod.NexusModsProjectId))
                            mod.HasUpdate = false;
                    }
                    finally
                    {
                        mod.IsCheckingUpdate = false;
                    }
                    continue;
                }

                Log.Info($"[CheckModUpdates] MOD {mod.Name} 没有 UpdateKeys，跳过");
                mod.HasUpdate = false;
                mod.UpdateSource = "None";
                continue;
            }

            Log.Info($"[CheckModUpdates] MOD {mod.Name} 有 {mod.Manifest.UpdateKeys.Count} 个 UpdateKey: {string.Join(", ", mod.Manifest.UpdateKeys)}");

            try
            {
                mod.IsCheckingUpdate = true;

                // 按优先级排序：Curseforge > NexusMods > GitHub
                var prioritizedKeys = mod.Manifest.UpdateKeys
                    .Select(k => new
                    {
                        Key = k,
                        Source = k.Split(new[] { ':' }, 2)[0].ToUpperInvariant(),
                        FullKey = k
                    })
                    .OrderBy(x => x.Source switch
                    {
                        "CURSEFORGE" => 0,  // 最高优先级
                        "NEXUS" => 1,        // 次优先级
                        "GITHUB" => 2,       // 第三优先级
                        _ => 99              // 其他最低
                    })
                    .ToList();

                Log.Info($"[CheckModUpdates] MOD {mod.Name} UpdateKeys 优先级排序: {string.Join(" -> ", prioritizedKeys.Select(x => $"{x.Source}({x.FullKey})"))}");

                bool sourceResolved = false;

                foreach (var keyItem in prioritizedKeys)
                {
                    if (sourceResolved)
                    {
                        Log.Info($"[CheckModUpdates] MOD {mod.Name} 已找到更新源，停止检查其他源");
                        break;
                    }

                    var updateKey = keyItem.FullKey;
                    var parts = updateKey.Split(new[] { ':' }, 2);
                    if (parts.Length != 2)
                    {
                        Log.Warn($"[CheckModUpdates] MOD {mod.Name} UpdateKey 格式无效: {updateKey}");
                        continue;
                    }

                    var source = parts[0];
                    var identifier = parts[1];

                    Log.Info($"[CheckModUpdates] MOD {mod.Name} 处理 UpdateKey: {source} | {identifier}");

                    switch (source.ToUpperInvariant())
                    {
                        case "CURSEFORGE":
                            // 解析Curseforge项目ID
                            var curseforgeId = identifier.Split('/')[0]; // "Curseforge:1234" -> "1234"
                            mod.CurseforgeProjectId = curseforgeId;
                            mod.UpdateSource = "Curseforge";

                            Log.Info($"[CheckModUpdates] MOD {mod.Name} 找到 Curseforge ID: {curseforgeId}");

                            sourceResolved = true;
                            if (!await TryCheckCurseforgeUpdateAsync(mod, curseforgeId))
                                mod.HasUpdate = false;
                            break;

                        case "NEXUS":
                            // 解析NexusMods项目ID
                            var nexusId = identifier.Split('/')[0]; // "Nexus:1234" -> "1234"
                            mod.NexusModsProjectId = nexusId;
                            mod.UpdateSource = "NexusMods";

                            Log.Info($"[CheckModUpdates] MOD {mod.Name} 找到 NexusMods ID: {nexusId}");

                            sourceResolved = true;
                            if (!await TryCheckNexusUpdateAsync(mod, nexusId))
                                mod.HasUpdate = false;
                            break;

                        case "GITHUB":
                            mod.UpdateSource = "GitHub";

                            Log.Info($"[CheckModUpdates] MOD {mod.Name} 找到 GitHub 源: {identifier}");

                            // TODO: 调用GitHub API检查更新
                            mod.HasUpdate = false;
                            sourceResolved = true; // 已找到更新源
                            break;

                        default:
                            Log.Warn($"[CheckModUpdates] MOD {mod.Name} 未知的 UpdateKey 源: {source}");
                            break;
                    }

                    mod.LastUpdateCheck = DateTime.UtcNow;
                }

                // 如果没有找到任何更新源
                if (!sourceResolved)
                {
                    Log.Info($"[CheckModUpdates] MOD {mod.Name} 未找到有效的更新源");
                    mod.UpdateSource = "None";
                    mod.HasUpdate = false;
                }
                else
                {
                    Log.Info($"[CheckModUpdates] MOD {mod.Name} 更新源: {mod.UpdateSource}, CurseforgeId: {mod.CurseforgeProjectId}, NexusId: {mod.NexusModsProjectId}");
                }
            }
            catch (NexusModsTokenExpiredException)
            {
                // Token过期，只显示一次通知，不修改UpdateSource
                if (!tokenExpiredShown)
                {
                    Log.Warn($"[CheckModUpdates] NexusMods Token 已过期（跳过MOD: {mod.Name}）");
                    mod.HasUpdate = false;
                    // 不修改UpdateSource，保持为"NexusMods"，重新登录后可以正常检查

                    tokenExpiredShown = true;

                    // 清除过期的Token
                    var settings = SVL.Core.Config.AppConfig.GetSettings();
                    if (!string.IsNullOrEmpty(settings.NexusModsOAuthToken))
                    {
                        settings.NexusModsOAuthToken = null;
                        settings.NexusModsOAuthRefreshToken = null;
                        SVL.Core.Config.AppConfig.SaveSettings(settings);
                        Log.Info("[CheckModUpdates] 已清除过期 NexusMods Token");
                    }
                }
                // 后续的NexusMods MOD也跳过，但不重复记录日志
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"[CheckModUpdates] MOD {mod.Name} 检查更新失败");
                mod.HasUpdate = false;
                mod.UpdateSource = "Error";
            }
            finally
            {
                mod.IsCheckingUpdate = false;
            }
        }

        Log.Info($"[CheckModUpdates] 完成检查 {mods.Count} 个 MOD 的更新");

        // 如果Token已过期，返回特殊值让调用者知道
        if (tokenExpiredShown)
        {
            Log.Warn("[CheckModUpdates] 检测到 Token 过期，将在调用层显示通知");
            throw new NexusModsTokenExpiredException();
        }
    }

    private async Task<bool> TryCheckNexusUpdateAsync(SdVMod mod, string nexusId)
    {
        var modId = ExtractLongId(nexusId);
        if (modId <= 0)
        {
            Log.Warn($"[CheckModUpdates] MOD {mod.Name} Nexus ID 无效: {nexusId}");
            return false;
        }

        // 检查Token是否存在（如果Token被清除，说明已过期，跳过检查）
        var settings = SVL.Core.Config.AppConfig.GetSettings();
        if (string.IsNullOrEmpty(settings.NexusModsOAuthToken))
        {
            Log.Info($"[CheckModUpdates] MOD {mod.Name} NexusMods Token 为空，跳过检查");
            return false;
        }

        var files = await NexusModsService.GetModFilesAsync(modId);
        if (files == null || files.Count == 0)
        {
            Log.Info($"[CheckModUpdates] MOD {mod.Name} Nexus 无可用文件");
            return false;
        }

        var latest = files
            .Where(f => !IsNexusOptionalFile(f))
            .OrderByDescending(ParseNexusUploadedTime)
            .ThenByDescending(f => f.GetEffectiveDownloadCount())
            .FirstOrDefault() ?? files.OrderByDescending(ParseNexusUploadedTime).FirstOrDefault();

        if (latest == null)
            return false;

        var latestVersion = string.IsNullOrWhiteSpace(latest.Version)
            ? ExtractVersionFromText(latest.FileName ?? latest.Name)
            : latest.Version;

        mod.LatestVersion = latestVersion ?? string.Empty;
        mod.UpdateUrl = $"https://www.nexusmods.com/stardewvalley/mods/{modId}?tab=files&file_id={latest.GetFileIdLong()}";

        var hasUpdate = IsVersionNewer(latestVersion, mod.Version);
        mod.HasUpdate = hasUpdate;

        Log.Info($"[CheckModUpdates] MOD {mod.Name} Nexus 当前={mod.Version}, 最新={latestVersion}, HasUpdate={hasUpdate}");
        return true;
    }

    private async Task<bool> TryCheckCurseforgeUpdateAsync(SdVMod mod, string curseforgeId)
    {
        var modId = ExtractIntId(curseforgeId);
        if (modId <= 0)
        {
            Log.Warn($"[CheckModUpdates] MOD {mod.Name} Curseforge ID 无效: {curseforgeId}");
            return false;
        }

        var files = await CurseforgeApiService.GetModFilesAsync(modId, 0, 100);
        if (files == null || files.Count == 0)
        {
            Log.Info($"[CheckModUpdates] MOD {mod.Name} Curseforge 无可用文件");
            return false;
        }

        var latest = files
            .Where(f => f.IsAvailable && !f.IsAlternate)
            .OrderByDescending(f => f.FileDate)
            .ThenBy(f => f.ReleaseType)
            .FirstOrDefault() ?? files.OrderByDescending(f => f.FileDate).FirstOrDefault();

        if (latest == null)
            return false;

        var latestVersion = ExtractVersionFromText(latest.DisplayName) ?? ExtractVersionFromText(latest.FileName);
        mod.LatestVersion = latestVersion ?? string.Empty;
        mod.UpdateUrl = string.IsNullOrWhiteSpace(latest.DownloadUrl)
            ? CurseforgeApiService.GetFileDownloadUrl(latest.Id)
            : latest.DownloadUrl;

        var hasUpdate = IsVersionNewer(latestVersion, mod.Version);
        mod.HasUpdate = hasUpdate;

        Log.Info($"[CheckModUpdates] MOD {mod.Name} Curseforge 当前={mod.Version}, 最新={latestVersion}, HasUpdate={hasUpdate}");
        return true;
    }

    private static bool IsNexusOptionalFile(NexusModFile file)
    {
        if (file == null || file.Categories == null || file.Categories.Count == 0)
            return false;

        return file.Categories.Any(c =>
            !string.IsNullOrWhiteSpace(c.Name)
            && c.Name.IndexOf("optional", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static DateTime ParseNexusUploadedTime(NexusModFile file)
    {
        if (file == null || string.IsNullOrWhiteSpace(file.UploadedTime))
            return DateTime.MinValue;

        if (DateTime.TryParse(file.UploadedTime, out var dt))
            return dt;

        return DateTime.MinValue;
    }

    private static string ExtractVersionFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var match = Regex.Match(text, @"\d+(?:\.\d+){1,4}");
        return match.Success ? match.Value : string.Empty;
    }

    private static bool IsVersionNewer(string latestVersion, string currentVersion)
    {
        if (string.IsNullOrWhiteSpace(latestVersion) || string.IsNullOrWhiteSpace(currentVersion))
            return false;

        var latest = Regex.Matches(latestVersion, @"\d+").Cast<Match>().Select(m => int.Parse(m.Value)).ToList();
        var current = Regex.Matches(currentVersion, @"\d+").Cast<Match>().Select(m => int.Parse(m.Value)).ToList();

        if (latest.Count == 0 || current.Count == 0)
            return !string.Equals(latestVersion.Trim(), currentVersion.Trim(), StringComparison.OrdinalIgnoreCase);

        var max = Math.Max(latest.Count, current.Count);
        for (var i = 0; i < max; i++)
        {
            var l = i < latest.Count ? latest[i] : 0;
            var c = i < current.Count ? current[i] : 0;
            if (l > c)
                return true;
            if (l < c)
                return false;
        }

        return false;
    }

    private static long ExtractLongId(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return 0;

        if (long.TryParse(raw, out var parsed) && parsed > 0)
            return parsed;

        var match = Regex.Match(raw, @"(\d+)(?!.*\d)");
        if (match.Success && long.TryParse(match.Groups[1].Value, out var extracted))
            return extracted;

        return 0;
    }

    private static int ExtractIntId(string raw)
    {
        var value = ExtractLongId(raw);
        return value > int.MaxValue ? 0 : (int)value;
    }

    private static string NormalizeId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return new string(value.Where(char.IsDigit).ToArray());
    }

    private static SvlSourceMetadata? TryLoadSourceCredential(string modDir)
    {
        return SvlSourceMetadataStore.TryReadFromDirectory(modDir);
    }

    private static string NormalizePlatform(string? platform)
    {
        if (string.IsNullOrWhiteSpace(platform))
            return string.Empty;

        if (string.Equals(platform, "Curseforge", StringComparison.OrdinalIgnoreCase))
            return "Curseforge";

        if (string.Equals(platform, "NexusMods", StringComparison.OrdinalIgnoreCase)
            || string.Equals(platform, "Nexus", StringComparison.OrdinalIgnoreCase))
            return "NexusMods";

        return platform.Trim();
    }

    private static bool MovePathToRecycleBin(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        // SHFileOperation 需要以双\0结尾
        var from = path + "\0\0";
        var fileOp = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = from,
            pTo = null,
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
            fAnyOperationsAborted = false,
            hNameMappings = IntPtr.Zero,
            lpszProgressTitle = null
        };

        var result = SHFileOperation(ref fileOp);
        return result == 0 && !fileOp.fAnyOperationsAborted;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string lpszProgressTitle;
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    /// <summary>
    /// 检测嵌套文件夹问题（manifest.json 在子目录中）
    /// 支持多层嵌套检测和 manifest.json 解析失败检测
    /// </summary>
    public async Task<List<NestedFolderIssue>> DetectNestedFolderIssuesAsync(string modsPath)
    {
        var issues = new List<NestedFolderIssue>();

        if (!Directory.Exists(modsPath))
            return issues;

        var modDirectories = Directory.GetDirectories(modsPath);

        foreach (var modDir in modDirectories)
        {
            try
            {
                var manifestPath = Path.Combine(modDir, "manifest.json");

                // 情况1: 根目录有 manifest.json - 检查是否能正确解析
                if (File.Exists(manifestPath))
                {
                    // 尝试解析 manifest.json
                    var parseResult = await TryParseManifestAsync(manifestPath);
                    if (!parseResult.IsValid)
                    {
                        // manifest.json 存在但解析失败
                        issues.Add(new NestedFolderIssue
                        {
                            ParentFolderPath = modDir,
                            NestedFolderPath = null,
                            ParentFolderName = Path.GetFileName(modDir),
                            NestedFolderName = string.Empty,
                            ModName = parseResult.ModName ?? Path.GetFileName(modDir),
                            HasOtherFiles = false,
                            IssueType = NestedFolderIssueType.ManifestParseError,
                            ParseErrorMessage = parseResult.ErrorMessage
                        });
                    }
                    continue;
                }

                // 情况2: 根目录没有 manifest.json - 递归搜索子目录
                var (nestedManifestPath, depth) = FindManifestRecursively(modDir, maxDepth: 5);

                if (nestedManifestPath != null)
                {
                    // 找到嵌套的 manifest.json
                    var nestedDir = Directory.GetParent(nestedManifestPath)?.FullName;

                    // 检查根目录是否有其他文件
                    var rootFiles = Directory.GetFiles(modDir);
                    var hasOtherFiles = rootFiles.Length > 0;

                    // 读取嵌套的 manifest 获取 MOD 名称
                    string? modName = null;
                    try
                    {
                        var manifestJson = await FileEx.ReadAllTextAsync(nestedManifestPath);
                        var options = new JsonSerializerOptions
                        {
                            ReadCommentHandling = JsonCommentHandling.Skip,
                            AllowTrailingCommas = true,
                            PropertyNameCaseInsensitive = true
                        };
                        var manifest = System.Text.Json.JsonSerializer.Deserialize<SdVModManifest>(manifestJson, options);
                        modName = manifest?.Name;
                    }
                    catch
                    {
                        modName = Path.GetFileName(nestedDir);
                    }

                    issues.Add(new NestedFolderIssue
                    {
                        ParentFolderPath = modDir,
                        NestedFolderPath = nestedDir ?? string.Empty,
                        ParentFolderName = Path.GetFileName(modDir),
                        NestedFolderName = nestedDir != null ? Path.GetFileName(nestedDir) : string.Empty,
                        ModName = modName ?? Path.GetFileName(modDir),
                        HasOtherFiles = hasOtherFiles,
                        IssueType = NestedFolderIssueType.NestedManifest,
                        NestingDepth = depth
                    });
                }
                else
                {
                    // 情况3: 完全找不到 manifest.json
                    issues.Add(new NestedFolderIssue
                    {
                        ParentFolderPath = modDir,
                        NestedFolderPath = null,
                        ParentFolderName = Path.GetFileName(modDir),
                        NestedFolderName = string.Empty,
                        ModName = Path.GetFileName(modDir),
                        HasOtherFiles = false,
                        IssueType = NestedFolderIssueType.NoManifest
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[NestedFolder] 检测失败: {modDir}", ex);
            }
        }

        Log.Info($"[NestedFolder] 检测到 {issues.Count} 个嵌套文件夹问题");
        return issues;
    }

    /// <summary>
    /// 递归查找 manifest.json
    /// </summary>
    private (string? manifestPath, int depth) FindManifestRecursively(string rootDir, int maxDepth)
    {
        return FindManifestRecursively(rootDir, 0, maxDepth);
    }

    private (string? manifestPath, int depth) FindManifestRecursively(string currentDir, int currentDepth, int maxDepth)
    {
        if (currentDepth > maxDepth)
            return (null, currentDepth);

        // 检查当前目录
        var manifestPath = Path.Combine(currentDir, "manifest.json");
        if (File.Exists(manifestPath))
            return (manifestPath, currentDepth);

        // 递归检查子目录
        var subDirs = Directory.GetDirectories(currentDir);
        foreach (var subDir in subDirs)
        {
            // 跳过 .disabled 目录
            if (subDir.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                continue;

            var (found, depth) = FindManifestRecursively(subDir, currentDepth + 1, maxDepth);
            if (found != null)
                return (found, depth);
        }

        return (null, currentDepth);
    }

    /// <summary>
    /// 尝试解析 manifest.json
    /// </summary>
    private async Task<(bool IsValid, string? ModName, string? ErrorMessage)> TryParseManifestAsync(string manifestPath)
    {
        try
        {
            var manifestJson = await FileEx.ReadAllTextAsync(manifestPath);
            var options = new JsonSerializerOptions
            {
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                PropertyNameCaseInsensitive = true
            };
            var manifest = System.Text.Json.JsonSerializer.Deserialize<SdVModManifest>(manifestJson, options);

            // 检查必要字段
            if (string.IsNullOrWhiteSpace(manifest?.Name) && string.IsNullOrWhiteSpace(manifest?.UniqueId))
            {
                return (false, manifest?.Name, "manifest.json 缺少 Name 或 UniqueId 字段");
            }

            return (true, manifest?.Name, null);
        }
        catch (Exception ex)
        {
            return (false, null, $"解析失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 修复嵌套文件夹问题
    /// </summary>
    public async Task<bool> FixNestedFolderAsync(NestedFolderIssue issue)
    {
        try
        {
            Log.Info($"[NestedFolder] 开始修复: {issue.ParentFolderName} (类型: {issue.IssueType})");

            // 对于无 manifest 的情况，无法自动修复
            if (issue.IssueType == NestedFolderIssueType.NoManifest)
            {
                Log.Warn($"[NestedFolder] 无法自动修复: {issue.ParentFolderName} - 未找到 manifest.json");
                return false;
            }

            // 对于 manifest 解析失败的情况，无法自动修复
            if (issue.IssueType == NestedFolderIssueType.ManifestParseError)
            {
                Log.Warn($"[NestedFolder] 无法自动修复: {issue.ParentFolderName} - {issue.ParseErrorMessage}");
                return false;
            }

            // 对于嵌套 manifest 的情况，移动文件
            if (issue.IssueType == NestedFolderIssueType.NestedManifest)
            {
                if (string.IsNullOrEmpty(issue.NestedFolderPath))
                {
                    Log.Warn($"[NestedFolder] 修复失败: 嵌套路径为空");
                    return false;
                }

                // 将嵌套文件夹中的所有文件移动到父文件夹
                var nestedDir = new DirectoryInfo(issue.NestedFolderPath);
                var parentDir = new DirectoryInfo(issue.ParentFolderPath);

                // 移动所有文件
                foreach (var file in nestedDir.GetFiles())
                {
                    var destPath = Path.Combine(parentDir.FullName, file.Name);
                    if (File.Exists(destPath))
                        File.Delete(destPath);
                    file.MoveTo(destPath);
                }

                // 移动所有子目录
                foreach (var dir in nestedDir.GetDirectories())
                {
                    var destPath = Path.Combine(parentDir.FullName, dir.Name);
                    if (Directory.Exists(destPath))
                        Directory.Delete(destPath, true);
                    dir.MoveTo(destPath);
                }

                // 删除空的嵌套文件夹
                try
                {
                    Directory.Delete(issue.NestedFolderPath, false);
                }
                catch
                {
                    // 忽略删除错误
                }

                Log.Info($"[NestedFolder] ✓ 修复成功: {issue.ParentFolderName}");
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"[NestedFolder] 修复失败: {issue.ParentFolderName}", ex);
            return false;
        }
    }

    /// <summary>
    /// 嵌套文件夹问题信息
    /// </summary>
    public class NestedFolderIssue
    {
        public string ParentFolderPath { get; set; } = string.Empty;
        public string NestedFolderPath { get; set; } = string.Empty;
        public string ParentFolderName { get; set; } = string.Empty;
        public string NestedFolderName { get; set; } = string.Empty;
        public string ModName { get; set; } = string.Empty;
        public bool HasOtherFiles { get; set; }

        /// <summary>
        /// 问题类型
        /// </summary>
        public NestedFolderIssueType IssueType { get; set; } = NestedFolderIssueType.NestedManifest;

        /// <summary>
        /// 嵌套深度
        /// </summary>
        public int NestingDepth { get; set; } = 0;

        /// <summary>
        /// 解析错误信息（当 IssueType 为 ManifestParseError 时）
        /// </summary>
        public string? ParseErrorMessage { get; set; }
    }

    /// <summary>
    /// 嵌套文件夹问题类型
    /// </summary>
    public enum NestedFolderIssueType
    {
        /// <summary>嵌套的 manifest.json</summary>
        NestedManifest,

        /// <summary>完全找不到 manifest.json</summary>
        NoManifest,

        /// <summary>manifest.json 解析失败</summary>
        ManifestParseError
    }
}
