using System;
using System.Collections.Generic;
using System.Linq;
using SVL.Core.Stardew.Mod;

namespace SVL.Core.Stardew.Mod.Dependency;

public class ModConflictDetector
{
    public class ConflictResult
    {
        public string ModId1 { get; set; }
        public string ModId2 { get; set; }
        public ConflictType Type { get; set; }
        public string Description { get; set; }
    }

    public enum ConflictType
    {
        DuplicateId,
        FileConflict,
        DependencyConflict,
        VersionMismatch
    }

    public static List<ConflictResult> DetectConflicts(List<SdVMod> mods)
    {
        var conflicts = new List<ConflictResult>();

        var uniqueIds = mods.Where(m => m.IsEnabled)
                                .Select(m => m.UniqueId)
                                .Where(id => !string.IsNullOrEmpty(id))
                                .ToList();

        var duplicates = uniqueIds.GroupBy(id => id)
                                    .Where(g => g.Count() > 1)
                                    .SelectMany(g => g)
                                    .ToList();

        foreach (var duplicate in duplicates)
        {
            var modsWithDuplicateId = mods.Where(m => m.UniqueId == duplicate)
                                                       .ToList();

            if (modsWithDuplicateId.Count > 1)
            {
                for (int i = 1; i < modsWithDuplicateId.Count; i++)
                {
                    for (int j = i + 1; j < modsWithDuplicateId.Count; j++)
                    {
                        conflicts.Add(new ConflictResult
                        {
                            ModId1 = modsWithDuplicateId[i - 1].UniqueId,
                            ModId2 = modsWithDuplicateId[j - 1].UniqueId,
                            Type = ConflictType.DuplicateId,
                            Description = $"Duplicate UniqueID: {duplicate}"
                        });
                    }
                }
            }
        }

        var dependencyConflicts = ModDependencyResolver.ResolveDependencies(mods)
            .Where(r => r.Status != ModDependencyResolver.ResolutionStatus.Satisfied)
            .Select(r => new ConflictResult
            {
                ModId1 = r.ModId,
                ModId2 = r.Version,
                Type = ConflictType.DependencyConflict,
                Description = r.Message
            })
            .ToList();

        conflicts.AddRange(dependencyConflicts);

        var fileConflicts = DetectFileConflicts(mods);
        conflicts.AddRange(fileConflicts);

        return conflicts;
    }

    private static List<ConflictResult> DetectFileConflicts(List<SdVMod> mods)
    {
        var conflicts = new List<ConflictResult>();
        var enabledMods = mods.Where(m => m.IsEnabled).ToList();

        foreach (var mod1 in enabledMods)
        {
            foreach (var mod2 in enabledMods)
            {
                if (mod1.UniqueId == mod2.UniqueId)
                {
                    continue;
                }

                if (HasFileConflict(mod1, mod2))
                {
                    conflicts.Add(new ConflictResult
                    {
                        ModId1 = mod1.UniqueId,
                        ModId2 = mod2.UniqueId,
                        Type = ConflictType.FileConflict,
                        Description = $"File conflict between {mod1.Name} and {mod2.Name}"
                    });
                }
            }
        }

        return conflicts;
    }

    private static bool HasFileConflict(SdVMod mod1, SdVMod mod2)
    {
        var mod1Files = GetModFiles(mod1);
        var mod2Files = GetModFiles(mod2);

        var commonFiles = mod1Files.Intersect(mod2Files, StringComparer.OrdinalIgnoreCase).ToList();

        if (commonFiles.Count == 0)
        {
            return false;
        }

        var conflictingFiles = commonFiles.Where(f => !IsAssetFile(f)).ToList();

        return conflictingFiles.Count > 0;
    }

    private static List<string> GetModFiles(SdVMod mod)
    {
        var files = new List<string>();
        var modPath = mod.ModPath;

        if (!System.IO.Directory.Exists(modPath))
        {
            return files;
        }

        files.AddRange(System.IO.Directory.GetFiles(modPath, "*.json", System.IO.SearchOption.AllDirectories));
        files.AddRange(System.IO.Directory.GetFiles(modPath, "*.dll", System.IO.SearchOption.AllDirectories));
        files.AddRange(System.IO.Directory.GetFiles(modPath, "*.png", System.IO.SearchOption.AllDirectories));

        var manifestPath = System.IO.Path.Combine(modPath, "manifest.json");
        if (System.IO.File.Exists(manifestPath))
        {
            files.Add(manifestPath);
        }

        return files;
    }

    private static bool IsAssetFile(string fileName)
    {
        var lowerName = fileName.ToLower();
        return lowerName == "icon.png" ||
               lowerName == "manifest.json" ||
               lowerName.EndsWith(".cs") ||
               lowerName.EndsWith(".dll");
    }

    public static bool HasConflicts(List<SdVMod> mods)
    {
        var conflicts = DetectConflicts(mods);
        return conflicts.Count > 0;
    }
}
