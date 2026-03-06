using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using SVL.Core.Logging;

namespace SVL.Core.Stardew.Mod;

public sealed class ModBackupMetadata
{
    public int SchemaVersion { get; set; } = 1;
    public string ModName { get; set; } = string.Empty;
    public string UniqueId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string OriginalRelativePath { get; set; } = string.Empty;
    public string BackupLabel { get; set; } = string.Empty;
    public DateTime BackupTimeUtc { get; set; } = DateTime.UtcNow;
}

public static class ModBackupService
{
    private const string BackupMetaFile = ".svl-backup.json";

    public static bool MovePathToRecycleBin(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            if (!File.Exists(path) && !Directory.Exists(path))
                return true;

            // SHFileOperation 需要以双\0结尾
            var from = path + "\0\0";
            var fileOp = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = from,
                pTo = null,
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
                fAnyOperationsAborted = false,
                hwnd = IntPtr.Zero,
                hNameMappings = IntPtr.Zero,
                lpszProgressTitle = null
            };

            var result = SHFileOperation(ref fileOp);
            return result == 0 && !fileOp.fAnyOperationsAborted;
        }
        catch (Exception ex)
        {
            Log.Warn($"[ModBackup] 移动到回收站失败: {path}, {ex.Message}");
            return false;
        }
    }

    public static string GetBackupRootPath(string modsPath)
    {
        var parent = Directory.GetParent(modsPath)?.FullName ?? modsPath;
        return Path.Combine(parent, "ModsBackup");
    }

    public static string EnsureBackupRoot(string modsPath)
    {
        var root = GetBackupRootPath(modsPath);
        if (!Directory.Exists(root))
            Directory.CreateDirectory(root);
        return root;
    }

    public static string BackupDirectory(string modsPath, string sourceDir, SdVMod mod = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
                return null;

            var backupRoot = EnsureBackupRoot(modsPath);
            var modName = mod?.Name;
            var version = mod?.Version;
            var uniqueId = mod?.UniqueId;

            if (string.IsNullOrWhiteSpace(modName) || string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(uniqueId))
            {
                var manifest = TryReadManifest(sourceDir);
                modName ??= manifest?.Name;
                version ??= manifest?.Version;
                uniqueId ??= manifest?.UniqueId;
            }

            modName ??= Path.GetFileName(sourceDir);
            version ??= "unknown";
            uniqueId ??= string.Empty;

            var safeName = SanitizeName(modName);
            var safeVersion = SanitizeName(version);
            var baseName = $"{safeName}_v{safeVersion}";

            var candidateName = baseName;
            var index = 1;
            while (Directory.Exists(Path.Combine(backupRoot, candidateName)))
            {
                candidateName = $"{baseName}-副本{index}";
                index++;
            }

            var targetDir = Path.Combine(backupRoot, candidateName);
            DirectoryCopy(sourceDir, targetDir, true);

            var relative = string.Empty;
            try
            {
                relative = GetRelativePathPortable(modsPath, sourceDir);
            }
            catch
            {
                relative = Path.GetFileName(sourceDir);
            }

            var meta = new ModBackupMetadata
            {
                ModName = modName,
                Version = version,
                UniqueId = uniqueId,
                OriginalRelativePath = relative,
                BackupLabel = candidateName,
                BackupTimeUtc = DateTime.UtcNow
            };

            WriteMetadata(targetDir, meta);
            Log.Info($"[ModBackup] 已创建备份: {sourceDir} -> {targetDir}");
            return targetDir;
        }
        catch (Exception ex)
        {
            Log.Warn($"[ModBackup] 创建备份失败: {sourceDir}, {ex.Message}");
            return null;
        }
    }

    public static List<SdVMod> LoadBackups(string modsPath)
    {
        var result = new List<SdVMod>();
        var backupRoot = GetBackupRootPath(modsPath);

        if (!Directory.Exists(backupRoot))
            return result;

        foreach (var backupDir in Directory.GetDirectories(backupRoot))
        {
            try
            {
                var manifest = TryReadManifest(backupDir);
                var meta = ReadMetadata(backupDir);
                var fallbackName = Path.GetFileName(backupDir);

                var mod = new SdVMod
                {
                    Id = $"backup:{fallbackName}",
                    Name = manifest?.Name ?? meta?.ModName ?? fallbackName,
                    Author = manifest?.Author ?? "",
                    Version = manifest?.Version ?? meta?.Version ?? "unknown",
                    Description = manifest?.Description ?? string.Empty,
                    UniqueId = manifest?.UniqueId ?? meta?.UniqueId ?? string.Empty,
                    ModPath = backupDir,
                    IsEnabled = true,
                    IsContentPack = manifest?.ContentPackFor != null,
                    InstalledDate = Directory.GetCreationTime(backupDir),
                    Manifest = manifest!,
                    Thumbnail = File.Exists(Path.Combine(backupDir, "icon.png")) ? Path.Combine(backupDir, "icon.png") : null,
                    IsBackupItem = true,
                    BackupTime = meta?.BackupTimeUtc.ToLocalTime() ?? Directory.GetCreationTime(backupDir),
                    BackupLabel = meta?.BackupLabel ?? fallbackName,
                    OriginalRelativePath = meta?.OriginalRelativePath ?? string.Empty
                };

                result.Add(mod);
            }
            catch (Exception ex)
            {
                Log.Warn($"[ModBackup] 读取备份失败: {backupDir}, {ex.Message}");
            }
        }

        return result.OrderByDescending(m => m.BackupTime ?? DateTime.MinValue).ToList();
    }

    public static bool SwapBackupWithActive(string modsPath, SdVMod backupMod, out string message)
    {
        message = string.Empty;

        if (backupMod == null || string.IsNullOrWhiteSpace(backupMod.ModPath) || !Directory.Exists(backupMod.ModPath))
        {
            message = "备份目录不存在";
            return false;
        }

        var activePath = FindActiveModPath(modsPath, backupMod, backupMod.ModPath);

        if (string.IsNullOrWhiteSpace(activePath))
        {
            var relative = string.IsNullOrWhiteSpace(backupMod.OriginalRelativePath)
                ? Path.GetFileName(backupMod.ModPath)
                : backupMod.OriginalRelativePath;

            var targetPath = Path.Combine(modsPath, relative);
            var parent = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(parent) && !Directory.Exists(parent))
                Directory.CreateDirectory(parent);

            if (Directory.Exists(targetPath))
            {
                message = "目标位置已存在同名目录，无法恢复";
                return false;
            }

            Directory.Move(backupMod.ModPath, targetPath);
            message = "已从备份恢复到 Mods";
            return true;
        }

        var tempPath = Path.Combine(Path.GetDirectoryName(activePath) ?? modsPath, $"{Path.GetFileName(activePath)}.swap_{Guid.NewGuid():N}");
        Directory.Move(activePath, tempPath);
        Directory.Move(backupMod.ModPath, activePath);
        Directory.Move(tempPath, backupMod.ModPath);

        // 更新交换后新备份（原 active）的元信息
        var activeManifest = TryReadManifest(backupMod.ModPath);
        var activeRelative = GetRelativePathPortable(modsPath, activePath);
        var metadata = new ModBackupMetadata
        {
            ModName = activeManifest?.Name ?? Path.GetFileName(activePath),
            Version = activeManifest?.Version ?? "unknown",
            UniqueId = activeManifest?.UniqueId ?? string.Empty,
            OriginalRelativePath = activeRelative,
            BackupLabel = Path.GetFileName(backupMod.ModPath),
            BackupTimeUtc = DateTime.UtcNow
        };
        WriteMetadata(backupMod.ModPath, metadata);

        message = "已执行备份与 Mods 的互换替换";
        return true;
    }

    public static SdVMod FindActiveMod(string modsPath, SdVMod backupMod)
    {
        var path = FindActiveModPath(modsPath, backupMod, backupMod.ModPath);
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var manifest = TryReadManifest(path);
        return new SdVMod
        {
            Id = Path.GetFileName(path),
            Name = manifest?.Name ?? Path.GetFileName(path),
            Version = manifest?.Version ?? "unknown",
            UniqueId = manifest?.UniqueId ?? string.Empty,
            ModPath = path,
            InstalledDate = Directory.GetCreationTime(path),
            Manifest = manifest!
        };
    }

    private static string FindActiveModPath(string modsPath, SdVMod referenceMod, string excludedPath)
    {
        if (!Directory.Exists(modsPath))
            return null;

        var manifests = Directory.GetFiles(modsPath, "manifest.json", SearchOption.AllDirectories);
        foreach (var manifestPath in manifests)
        {
            var dir = Path.GetDirectoryName(manifestPath);
            if (string.IsNullOrWhiteSpace(dir))
                continue;

            if (dir.StartsWith(GetBackupRootPath(modsPath), StringComparison.OrdinalIgnoreCase))
                continue;

            if (dir.Equals(excludedPath, StringComparison.OrdinalIgnoreCase))
                continue;

            var manifest = TryReadManifest(dir);
            if (manifest == null)
                continue;

            if (!string.IsNullOrWhiteSpace(referenceMod.UniqueId)
                && string.Equals(manifest.UniqueId, referenceMod.UniqueId, StringComparison.OrdinalIgnoreCase))
            {
                return dir;
            }

            if (string.Equals(manifest.Name, referenceMod.Name, StringComparison.OrdinalIgnoreCase))
            {
                return dir;
            }
        }

        return null;
    }

    private static SdVModManifest TryReadManifest(string modDir)
    {
        try
        {
            var manifestPath = Path.Combine(modDir, "manifest.json");
            if (!File.Exists(manifestPath))
                return null;

            var json = File.ReadAllText(manifestPath);
            var options = new JsonSerializerOptions
            {
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                PropertyNameCaseInsensitive = true
            };
            return JsonSerializer.Deserialize<SdVModManifest>(json, options);
        }
        catch
        {
            return null;
        }
    }

    private static string SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "unknown";

        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var sanitized = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }

    private static ModBackupMetadata ReadMetadata(string backupDir)
    {
        try
        {
            var path = Path.Combine(backupDir, BackupMetaFile);
            if (!File.Exists(path))
                return null;

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ModBackupMetadata>(json);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteMetadata(string backupDir, ModBackupMetadata metadata)
    {
        var path = Path.Combine(backupDir, BackupMetaFile);
        var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
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

    private static void DirectoryCopy(string sourceDirName, string destDirName, bool copySubDirs)
    {
        var dir = new DirectoryInfo(sourceDirName);
        if (!dir.Exists)
            throw new DirectoryNotFoundException(sourceDirName);

        Directory.CreateDirectory(destDirName);

        foreach (var file in dir.GetFiles())
        {
            var targetFilePath = Path.Combine(destDirName, file.Name);
            file.CopyTo(targetFilePath, true);
        }

        if (!copySubDirs)
            return;

        foreach (var subdir in dir.GetDirectories())
        {
            var tempPath = Path.Combine(destDirName, subdir.Name);
            DirectoryCopy(subdir.FullName, tempPath, true);
        }
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
}
