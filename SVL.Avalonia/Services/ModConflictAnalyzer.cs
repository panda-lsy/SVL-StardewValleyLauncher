using System.Globalization;
using System.Text.RegularExpressions;
using SVL.Avalonia.ViewModels;

namespace SVL.Avalonia.Services;

/// <summary>本地 Mod 冲突类型。</summary>
public enum ModConflictKind
{
    DuplicateId,
    MissingDependency,
    DisabledDependency,
    VersionMismatch,
    CircularDependency,
    FileConflict,
    CommunityHardConflict,
    CommunityFunctionalOverlap
}

/// <summary>一次本地 Mod 冲突的可展示结果。</summary>
public sealed record ModConflictResult(
    ModConflictKind Kind,
    string ModName,
    string RelatedModName,
    string Description)
{
    public string KindText => Kind switch
    {
        ModConflictKind.DuplicateId => "重复 ID",
        ModConflictKind.MissingDependency => "缺少前置",
        ModConflictKind.DisabledDependency => "前置已禁用",
        ModConflictKind.VersionMismatch => "前置版本不满足",
        ModConflictKind.CircularDependency => "循环依赖",
        ModConflictKind.FileConflict => "文件冲突",
        ModConflictKind.CommunityHardConflict => "社区冲突",
        ModConflictKind.CommunityFunctionalOverlap => "功能重复",
        _ => "冲突"
    };

    public string DisplayText => string.IsNullOrWhiteSpace(RelatedModName)
        ? $"[{KindText}] {ModName}：{Description}"
        : $"[{KindText}] {ModName} ↔ {RelatedModName}：{Description}";
}

/// <summary>
/// 一个 Mod 及其对应的 SVL 本地化社区条目。
/// 冲突检测只使用条目中的 hardConflicts/functionalOverlaps，
/// 不把文件名、svl-source.json 或其它运行时文件当成冲突依据。
/// </summary>
public sealed record ModCommunityConflictSource(
    ModManageItem Mod,
    CommunityLocalizationEntry Entry);

/// <summary>
/// 对已安装 Mod 做只读冲突分析。
/// 该类不改变文件或启用状态，方便在 UI 外单独回归测试。
/// </summary>
public static class ModConflictAnalyzer
{
    private static readonly Regex s_versionPartRegex = new(@"\d+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 保留旧的结构性诊断入口（重复 ID、依赖和循环依赖）。不读取 Mod 目录文件；
    /// 用户界面的冲突检测必须使用 <see cref="AnalyzeCommunity"/>，只依据社区字段。
    /// </summary>
    public static IReadOnlyList<ModConflictResult> Analyze(IEnumerable<ModManageItem> source)
    {
        var candidates = source
            .Where(IsCandidateMod)
            .ToList();
        var enabledMods = candidates
            .Where(item => item.IsEnabled)
            .ToList();

        var conflicts = new List<ModConflictResult>();
        DetectDuplicateIds(enabledMods, conflicts);
        DetectDependencyConflicts(enabledMods, candidates, conflicts);
        DetectCircularDependencies(enabledMods, candidates, conflicts);

        return conflicts
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.ModName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.RelatedModName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Description, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 按 SVL 本地化社区的冲突字段检测当前已安装 Mod。
    ///
    /// 社区条目的关系只描述“可能冲突”的 Mod，只有关系双方都存在且都启用时
    /// 才报告结果。这样不会把未安装的关系项误报成冲突，也不会扫描 Mod 目录
    /// 中的文件，因此 svl-source.json 等来源元数据不会制造海量假冲突。
    /// </summary>
    public static IReadOnlyList<ModConflictResult> AnalyzeCommunity(
        IEnumerable<ModCommunityConflictSource> source)
    {
        var entries = (source ?? [])
            .Where(item => item.Mod != null &&
                           item.Entry != null &&
                           IsCandidateMod(item.Mod) &&
                           item.Mod.IsEnabled)
            .GroupBy(item => GetModKey(item.Mod), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        var installed = entries.Select(item => item.Mod).ToList();
        var conflicts = new List<ModConflictResult>();
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in entries)
        {
            AddCommunityRelations(
                item,
                item.Entry.HardConflicts,
                ModConflictKind.CommunityHardConflict,
                installed,
                reported,
                conflicts);
            AddCommunityRelations(
                item,
                item.Entry.FunctionalOverlaps,
                ModConflictKind.CommunityFunctionalOverlap,
                installed,
                reported,
                conflicts);
        }

        return conflicts
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.ModName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.RelatedModName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Description, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddCommunityRelations(
        ModCommunityConflictSource source,
        IEnumerable<CommunityLocalizationRelation>? relations,
        ModConflictKind kind,
        IReadOnlyList<ModManageItem> installed,
        ISet<string> reported,
        ICollection<ModConflictResult> conflicts)
    {
        foreach (var relation in relations ?? [])
        {
            var related = FindRelatedMod(relation, installed, source.Mod);
            if (related == null)
            {
                continue;
            }

            var firstKey = GetModKey(source.Mod);
            var secondKey = GetModKey(related);
            var pairKey = string.Compare(firstKey, secondKey, StringComparison.OrdinalIgnoreCase) < 0
                ? $"{kind}:{firstKey}:{secondKey}"
                : $"{kind}:{secondKey}:{firstKey}";
            if (!reported.Add(pairKey))
            {
                continue;
            }

            var defaultDescription = kind == ModConflictKind.CommunityHardConflict
                ? "SVL 本地化社区标注为硬冲突"
                : "SVL 本地化社区标注为功能重复";
            conflicts.Add(new ModConflictResult(
                kind,
                GetModName(source.Mod),
                GetModName(related),
                string.IsNullOrWhiteSpace(relation.Reason)
                    ? defaultDescription
                    : relation.Reason.Trim()));
        }
    }

    private static ModManageItem? FindRelatedMod(
        CommunityLocalizationRelation relation,
        IReadOnlyList<ModManageItem> installed,
        ModManageItem source)
    {
        var relationId = relation.Id?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(relationId))
        {
            var byId = installed.FirstOrDefault(mod =>
                !ReferenceEquals(mod, source) &&
                GetIdentityValues(mod).Any(value =>
                    string.Equals(value, relationId, StringComparison.OrdinalIgnoreCase)));
            if (byId != null)
            {
                return byId;
            }
        }

        var relationName = relation.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(relationName))
        {
            return null;
        }

        return installed.FirstOrDefault(mod =>
            !ReferenceEquals(mod, source) &&
            GetNameValues(mod).Any(value =>
                string.Equals(value, relationName, StringComparison.OrdinalIgnoreCase)));
    }

    private static IEnumerable<string> GetIdentityValues(ModManageItem mod)
    {
        return new[]
        {
            mod.UniqueId,
            mod.CurseforgeProjectId,
            mod.NexusModsProjectId,
            mod.DirectoryName,
            mod.FolderName
        }.Where(value => !string.IsNullOrWhiteSpace(value))
         .Select(value => value.Trim());
    }

    private static IEnumerable<string> GetNameValues(ModManageItem mod)
    {
        return new[]
        {
            mod.DisplayName,
            mod.SourceDisplayName,
            mod.DirectoryName,
            mod.FolderName,
            mod.UniqueId
        }.Where(value => !string.IsNullOrWhiteSpace(value))
         .Select(value => value.Trim());
    }

    private static string GetModKey(ModManageItem mod)
    {
        return !string.IsNullOrWhiteSpace(mod.FullPath)
            ? mod.FullPath.Trim()
            : $"{mod.UniqueId}|{mod.DisplayName}";
    }

    private static bool IsCandidateMod(ModManageItem item)
    {
        // 复合 Mod 的父项只是分组头，文件实际归属于子项；子项由其父项代表，
        // 否则同一份文件会在父/子之间产生大量误报。
        return item.IsNormalItem &&
               !item.IsChildMod &&
               !item.IsCompositeParent &&
               !string.IsNullOrWhiteSpace(item.FullPath) &&
               Directory.Exists(item.FullPath);
    }

    private static void DetectDuplicateIds(
        IReadOnlyList<ModManageItem> mods,
        ICollection<ModConflictResult> conflicts)
    {
        foreach (var group in mods
                     .Where(item => !string.IsNullOrWhiteSpace(item.UniqueId))
                     .GroupBy(item => item.UniqueId.Trim(), StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            var duplicateMods = group
                .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (var firstIndex = 0; firstIndex < duplicateMods.Count - 1; firstIndex++)
            {
                for (var secondIndex = firstIndex + 1; secondIndex < duplicateMods.Count; secondIndex++)
                {
                    conflicts.Add(new ModConflictResult(
                        ModConflictKind.DuplicateId,
                        GetModName(duplicateMods[firstIndex]),
                        GetModName(duplicateMods[secondIndex]),
                        $"两者都使用 UniqueID“{group.Key}”"));
                }
            }
        }
    }

    private static void DetectDependencyConflicts(
        IReadOnlyList<ModManageItem> enabledMods,
        IReadOnlyList<ModManageItem> allMods,
        ICollection<ModConflictResult> conflicts)
    {
        var installedById = allMods
            .Where(item => !string.IsNullOrWhiteSpace(item.UniqueId))
            .GroupBy(item => item.UniqueId.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var mod in enabledMods)
        {
            foreach (var dependency in mod.DisplayDependencies.Where(item => item.IsRequired))
            {
                if (!installedById.TryGetValue(dependency.UniqueId, out var installed))
                {
                    conflicts.Add(new ModConflictResult(
                        ModConflictKind.MissingDependency,
                        GetModName(mod),
                        dependency.DisplayName,
                        $"需要“{dependency.DisplayText}”，但当前实例未安装"));
                    continue;
                }

                if (!installed.IsEnabled)
                {
                    conflicts.Add(new ModConflictResult(
                        ModConflictKind.DisabledDependency,
                        GetModName(mod),
                        GetModName(installed),
                        $"需要“{dependency.DisplayText}”，但该前置已禁用"));
                    continue;
                }

                if (IsVersionRequirementUnsatisfied(dependency.MinimumVersion, installed.Version))
                {
                    conflicts.Add(new ModConflictResult(
                        ModConflictKind.VersionMismatch,
                        GetModName(mod),
                        GetModName(installed),
                        $"需要最低版本 {dependency.MinimumVersion}，当前为 {installed.Version}"));
                }
            }
        }
    }

    private static void DetectCircularDependencies(
        IReadOnlyList<ModManageItem> enabledMods,
        IReadOnlyList<ModManageItem> allMods,
        ICollection<ModConflictResult> conflicts)
    {
        var installedById = allMods
            .Where(item => item.IsEnabled && !string.IsNullOrWhiteSpace(item.UniqueId))
            .GroupBy(item => item.UniqueId.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var states = new Dictionary<string, VisitState>(StringComparer.OrdinalIgnoreCase);
        var path = new List<ModManageItem>();
        var reportedCycles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(ModManageItem mod)
        {
            var key = GetDependencyKey(mod);
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            if (states.TryGetValue(key, out var state))
            {
                if (state != VisitState.Visiting)
                {
                    return;
                }

                var cycleStart = path.FindIndex(item =>
                    string.Equals(GetDependencyKey(item), key, StringComparison.OrdinalIgnoreCase));
                if (cycleStart < 0)
                {
                    return;
                }

                var cycle = path.Skip(cycleStart).ToList();
                var cycleKey = string.Join("|", cycle
                    .Select(GetDependencyKey)
                    .OrderBy(item => item, StringComparer.OrdinalIgnoreCase));
                if (reportedCycles.Add(cycleKey))
                {
                    var cycleNames = cycle
                        .Select(GetModName)
                        .Append(GetModName(mod))
                        .ToList();
                    conflicts.Add(new ModConflictResult(
                        ModConflictKind.CircularDependency,
                        GetModName(cycle[0]),
                        GetModName(mod),
                        $"依赖链“{string.Join(" → ", cycleNames)}”形成循环"));
                }

                return;
            }

            states[key] = VisitState.Visiting;
            path.Add(mod);
            foreach (var dependency in mod.DisplayDependencies.Where(item =>
                         item.IsRequired &&
                         !string.IsNullOrWhiteSpace(item.UniqueId) &&
                         installedById.TryGetValue(item.UniqueId, out _)))
            {
                if (!installedById.TryGetValue(dependency.UniqueId, out var dependencyMod))
                {
                    continue;
                }

                Visit(dependencyMod);
            }

            path.RemoveAt(path.Count - 1);
            states[key] = VisitState.Visited;
        }

        foreach (var mod in enabledMods)
        {
            Visit(mod);
        }
    }

    private static string GetDependencyKey(ModManageItem mod)
    {
        return string.IsNullOrWhiteSpace(mod.UniqueId)
            ? NormalizeRelativePath(mod.FullPath)
            : mod.UniqueId.Trim();
    }

    private static string NormalizeRelativePath(string path)
    {
        return path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Replace('\\', '/')
            .TrimStart('/');
    }

    private static bool IsVersionRequirementUnsatisfied(string? minimumVersion, string? installedVersion)
    {
        if (string.IsNullOrWhiteSpace(minimumVersion) || minimumVersion.Trim() == "*")
        {
            return false;
        }

        if (!TryGetVersionParts(minimumVersion, out var required) ||
            !TryGetVersionParts(installedVersion, out var installed))
        {
            // 版本字段不是标准数字版本时不武断报错；缺失/禁用状态仍会正常报告。
            return false;
        }

        var length = Math.Max(required.Count, installed.Count);
        for (var index = 0; index < length; index++)
        {
            var requiredPart = index < required.Count ? required[index] : 0;
            var installedPart = index < installed.Count ? installed[index] : 0;
            if (installedPart != requiredPart)
            {
                return installedPart < requiredPart;
            }
        }

        return false;
    }

    private static bool TryGetVersionParts(string? text, out List<int> parts)
    {
        parts = [];
        if (string.IsNullOrWhiteSpace(text) ||
            text.Equals("未知版本", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var matches = s_versionPartRegex.Matches(text);
        if (matches.Count == 0)
        {
            return false;
        }

        foreach (Match match in matches)
        {
            if (!int.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var part))
            {
                return false;
            }

            parts.Add(part);
        }

        return parts.Count > 0;
    }

    private enum VisitState
    {
        Visiting,
        Visited
    }

    private static string GetModName(ModManageItem item)
    {
        return string.IsNullOrWhiteSpace(item.DisplayName)
            ? (string.IsNullOrWhiteSpace(item.DirectoryName) ? item.UniqueId : item.DirectoryName)
            : item.DisplayName;
    }
}
