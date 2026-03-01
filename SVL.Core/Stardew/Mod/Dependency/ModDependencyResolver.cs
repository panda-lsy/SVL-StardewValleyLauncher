using System;
using System.Collections.Generic;
using System.Linq;
using SVL.Core.Stardew.Mod;

namespace SVL.Core.Stardew.Mod.Dependency;

public class ModDependencyResolver
{
    public class DependencyResolution
    {
        public string ModId { get; set; }
        public string Version { get; set; }
        public ResolutionStatus Status { get; set; }
        public string Message { get; set; }
    }

    public enum ResolutionStatus
    {
        Satisfied,
        MissingDependency,
        VersionConflict,
        CircularDependency
    }

    public static List<DependencyResolution> ResolveDependencies(List<SdVMod> mods)
    {
        var resolutions = new List<DependencyResolution>();
        var modMap = mods.ToDictionary(m => m.UniqueId, m => m);

        foreach (var mod in mods)
        {
            if (!mod.IsEnabled)
            {
                continue;
            }

            if (mod.Manifest?.Dependencies == null || mod.Manifest.Dependencies.Count == 0)
            {
                continue;
            }

            foreach (var dependency in mod.Manifest.Dependencies)
            {
                var resolution = CheckDependency(modMap, dependency, mod);
                resolutions.Add(resolution);
            }
        }

        return resolutions;
    }

    public static bool ValidateDependencies(List<SdVMod> mods)
    {
        var resolutions = ResolveDependencies(mods);
        return resolutions.All(r => r.Status == ResolutionStatus.Satisfied);
    }

    private static DependencyResolution CheckDependency(Dictionary<string, SdVMod> modMap, ModDependency dependency, SdVMod dependentMod)
    {
        if (!modMap.TryGetValue(dependency.UniqueId, out var dependencyMod))
        {
            return new DependencyResolution
            {
                ModId = dependency.UniqueId,
                Version = dependency.MinimumVersion ?? "*",
                Status = ResolutionStatus.MissingDependency,
                Message = $"Required mod '{dependency.UniqueId}' not found"
            };
        }

        if (!dependencyMod.IsEnabled)
        {
            return new DependencyResolution
            {
                ModId = dependency.UniqueId,
                Version = dependency.MinimumVersion ?? "*",
                Status = ResolutionStatus.MissingDependency,
                Message = $"Required mod '{dependency.UniqueId}' is disabled"
            };
        }

        if (!string.IsNullOrEmpty(dependency.MinimumVersion))
        {
            return new DependencyResolution
            {
                ModId = dependency.UniqueId,
                Version = "*",
                Status = ResolutionStatus.Satisfied,
                Message = string.Empty
            };
        }

        if (!IsVersionSatisfied(dependencyMod.Version, dependency.MinimumVersion))
        {
            return new DependencyResolution
            {
                ModId = dependency.UniqueId,
                Version = dependency.MinimumVersion,
                Status = ResolutionStatus.VersionConflict,
                Message = $"Version mismatch: requires {dependency.MinimumVersion}, found {dependencyMod.Version}"
            };
        }

        return new DependencyResolution
        {
            ModId = dependency.UniqueId,
            Version = dependency.MinimumVersion ?? "*",
            Status = ResolutionStatus.Satisfied,
            Message = string.Empty
        };
    }

    private static bool IsVersionSatisfied(string installedVersion, string requiredVersion)
    {
        if (string.IsNullOrEmpty(requiredVersion) || requiredVersion == "*")
        {
            return true;
        }

        return CompareVersions(installedVersion, requiredVersion) >= 0;
    }

    private static int CompareVersions(string version1, string version2)
    {
        var v1 = version1.Split('.');
        var v2 = version2.Split('.');

        for (int i = 0; i < Math.Max(v1.Length, v2.Length); i++)
        {
            var n1 = int.TryParse(v1[i], out var num1) ? num1 : 0;
            var n2 = int.TryParse(v2[i], out var num2) ? num2 : 0;

            if (n1 != n2)
            {
                return n1.CompareTo(n2);
            }
        }

        return 0;
    }

    public static List<SdVMod> GetLoadOrder(List<SdVMod> mods)
    {
        var sorted = new List<SdVMod>();
        var processed = new HashSet<string>();

        foreach (var mod in mods.Where(m => m.IsEnabled))
        {
            if (processed.Contains(mod.UniqueId))
            {
                continue;
            }

            if (HasCircularDependency(mod, mods, processed))
            {
                continue;
            }

            sorted.Add(mod);
            processed.Add(mod.UniqueId);
        }

        return sorted;
    }

    private static bool HasCircularDependency(SdVMod mod, List<SdVMod> allMods, HashSet<string> processed)
    {
        if (mod.Manifest?.Dependencies == null)
        {
            return false;
        }

        processed.Add(mod.UniqueId);

        foreach (var dep in mod.Manifest.Dependencies)
        {
            if (processed.Contains(dep.UniqueId))
            {
                return true;
            }

            var depMod = allMods.FirstOrDefault(m => m.UniqueId == dep.UniqueId);
            if (depMod != null && HasCircularDependency(depMod, allMods, processed))
            {
                return true;
            }
        }

        return false;
    }
}
