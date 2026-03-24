using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using SVL.Core.Stardew.Mod;

namespace SVL.Core.Stardew.Mod;

public class SdVMod : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _hasUpdate;
    private bool _isGroupExpanded;
    private bool _useLocalizedName = true;
    private bool _useLocalizedDescription = true;
    private List<ModDependencyLink> _displayLocalizedMods = [];

    public string Id { get; set; }
    public string Name { get; set; }
    public string Author { get; set; }
    public string Version { get; set; }
    public string Description { get; set; }
    public string UniqueId { get; set; }
    public string ModPath { get; set; }
    public bool IsEnabled { get; set; }
    public bool IsContentPack { get; set; }
    public DateTime InstalledDate { get; set; }
    public SdVModManifest Manifest { get; set; }
    public List<string> Dependencies { get; set; } = [];
    public List<string> ConflictingMods { get; set; } = [];
    public string Thumbnail { get; set; }
    public List<string> Tags { get; set; } = [];
    public ObservableCollection<string> CustomTags { get; } = [];
    public string LocalizedNameZhCn { get; set; } = string.Empty;
    public string LocalizedNameSource { get; set; } = string.Empty;
    public string LocalizedDescriptionZhCn { get; set; } = string.Empty;
    public string LocalizedDescriptionSource { get; set; } = string.Empty;
    public string LocalizationSourceUrl { get; set; } = string.Empty;
    public string LocalizationUpdatedAt { get; set; } = string.Empty;
    public bool IsBackupItem { get; set; }
    public DateTime? BackupTime { get; set; }
    public string BackupLabel { get; set; } = string.Empty;
    public string OriginalRelativePath { get; set; } = string.Empty;
    public bool IsChildMod { get; set; }
    public bool IsCompositeParent { get; set; }
    public string ParentModId { get; set; } = string.Empty;
    public string ParentModName { get; set; } = string.Empty;
    public ObservableCollection<SdVMod> ChildMods { get; } = [];
    public IEnumerable<string> AllTags => (Tags ?? [])
        .Concat(CustomTags ?? [])
        .Where(tag => !string.IsNullOrWhiteSpace(tag))
        .Distinct(StringComparer.OrdinalIgnoreCase);
    public IEnumerable<string> DisplayTags
    {
        get
        {
            var prefixTag = TryExtractPrefixCategory(FolderName);

            var folderDisplay = (Tags ?? [])
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => string.Equals(tag, prefixTag, StringComparison.OrdinalIgnoreCase)
                    ? $"🧩 {tag}"
                    : $"📂 {tag}");

            var customDisplay = (CustomTags ?? [])
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => $"🏷 {tag}");

            return folderDisplay.Concat(customDisplay);
        }
    }
    public bool HasAnyTag => DisplayTags.Any();
    public string TagsDisplay => !AllTags.Any() ? string.Empty : string.Join(" / ", AllTags);
    public bool HasChildren => ChildMods.Count > 0;
    public bool CanShowOnlineDetails => !IsChildMod;
    public string GroupToggleText => IsGroupExpanded ? "⌄" : "⌃";
    public bool HasLocalizedName => !string.IsNullOrWhiteSpace(LocalizedNameZhCn);
    public bool HasLocalizedDescription => !string.IsNullOrWhiteSpace(LocalizedDescriptionZhCn);
    public bool HasAnyLocalization => HasLocalizedName || HasLocalizedDescription;
    public bool IsUsingLocalizedText => UseLocalizedName || UseLocalizedDescription;
    public string DisplayName => UseLocalizedName && HasLocalizedName
        ? LocalizedNameZhCn
        : (!string.IsNullOrWhiteSpace(LocalizedNameSource) ? LocalizedNameSource : Name);
    public string DisplayDescription => UseLocalizedDescription && HasLocalizedDescription
        ? LocalizedDescriptionZhCn
        : (!string.IsNullOrWhiteSpace(LocalizedDescriptionSource) ? LocalizedDescriptionSource : Description);
    public string FolderName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ModPath))
                return string.Empty;

            return ModFolderNaming.NormalizeFolderName(ModPath);
        }
    }

    /// <summary>
    /// 源文件名（原始压缩包名称）
    /// </summary>
    public string SourceFileName { get; set; }

    /// <summary>
    /// NexusMods 项目ID
    /// </summary>
    public string NexusModsProjectId { get; set; }

    /// <summary>
    /// Curseforge 项目ID
    /// </summary>
    public string CurseforgeProjectId { get; set; }

    /// <summary>
    /// 更新来源（Curseforge/NexusMods/None）
    /// </summary>
    public string UpdateSource { get; set; }

    /// <summary>
    /// 最新可用版本（更新检测后填充）
    /// </summary>
    public string LatestVersion { get; set; }

    /// <summary>
    /// 更新下载URL（更新检测后填充）
    /// </summary>
    public string UpdateUrl { get; set; }

    /// <summary>
    /// 更新检查时间
    /// </summary>
    public DateTime? LastUpdateCheck { get; set; }

    /// <summary>
    /// 是否正在检查更新
    /// </summary>
    public bool IsCheckingUpdate { get; set; }

    /// <summary>
    /// 依赖项详细信息
    /// </summary>
    public List<ModDependency> DependencyDetails { get; set; } = [];

    /// <summary>
    /// 用于 UI 展示和导航的前置模组信息
    /// </summary>
    public List<ModDependencyLink> DisplayDependencies { get; set; } = [];

    /// <summary>
    /// 用于 UI 展示和导航的汉化模组信息
    /// </summary>
    public List<ModDependencyLink> DisplayLocalizedMods
    {
        get => _displayLocalizedMods;
        set
        {
            _displayLocalizedMods = value ?? [];
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasDisplayLocalizedMods));
        }
    }

    public bool HasDisplayLocalizedMods => DisplayLocalizedMods.Count > 0;

    /// <summary>
    /// 是否被选中（用于UI多选）
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// 是否有更新可用（通过UpdateKeys检测）
    /// </summary>
    public bool HasUpdate
    {
        get => _hasUpdate;
        set
        {
            if (_hasUpdate != value)
            {
                _hasUpdate = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsGroupExpanded
    {
        get => _isGroupExpanded;
        set
        {
            if (_isGroupExpanded != value)
            {
                _isGroupExpanded = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(GroupToggleText));
            }
        }
    }

    public bool UseLocalizedName
    {
        get => _useLocalizedName;
        set
        {
            if (_useLocalizedName != value)
            {
                _useLocalizedName = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(IsUsingLocalizedText));
            }
        }
    }

    public bool UseLocalizedDescription
    {
        get => _useLocalizedDescription;
        set
        {
            if (_useLocalizedDescription != value)
            {
                _useLocalizedDescription = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayDescription));
                OnPropertyChanged(nameof(IsUsingLocalizedText));
            }
        }
    }

    public void ApplyLocalization(SvlSourceLocalization? localization)
    {
        if (localization == null)
            return;

        LocalizedNameZhCn = localization.NameZhCn ?? string.Empty;
        LocalizedNameSource = localization.NameSource ?? string.Empty;
        LocalizedDescriptionZhCn = localization.DescriptionZhCn ?? string.Empty;
        LocalizedDescriptionSource = localization.DescriptionSource ?? string.Empty;
        LocalizationSourceUrl = localization.SourceUrl ?? string.Empty;
        LocalizationUpdatedAt = localization.UpdatedAt ?? string.Empty;

        if (HasLocalizedName)
            UseLocalizedName = true;
        if (HasLocalizedDescription)
            UseLocalizedDescription = true;

        OnPropertyChanged(nameof(HasLocalizedName));
        OnPropertyChanged(nameof(HasLocalizedDescription));
        OnPropertyChanged(nameof(HasAnyLocalization));
        OnPropertyChanged(nameof(IsUsingLocalizedText));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(DisplayDescription));
    }

    public void SetLocalizationLanguage(bool useLocalized)
    {
        UseLocalizedName = useLocalized;
        UseLocalizedDescription = useLocalized;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void NotifyTagChanged()
    {
        OnPropertyChanged(nameof(AllTags));
        OnPropertyChanged(nameof(DisplayTags));
        OnPropertyChanged(nameof(HasAnyTag));
        OnPropertyChanged(nameof(TagsDisplay));
    }

    private static string TryExtractPrefixCategory(string? folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
            return string.Empty;

        var trimmed = folderName.Trim();

        var squareMatch = Regex.Match(trimmed, @"^\[(?<name>[^\]]{1,64})\]");
        if (squareMatch.Success)
            return squareMatch.Groups["name"].Value.Trim();

        var cnSquareMatch = Regex.Match(trimmed, @"^【(?<name>[^】]{1,64})】");
        if (cnSquareMatch.Success)
            return cnSquareMatch.Groups["name"].Value.Trim();

        return string.Empty;
    }
}

public class ModDependencyLink
{
    public string UniqueId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string MinimumVersion { get; set; } = string.Empty;
    public bool IsRequired { get; set; } = true;
    public bool IsInstalled { get; set; }
    public bool IsInstalledAndEnabled { get; set; }
    public bool IsInstalledButDisabled { get; set; }
    public bool IsPlaceholder { get; set; }
    public string InstalledModId { get; set; } = string.Empty;
    public string InstalledModName { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;

    public string DisplayText
    {
        get
        {
            if (IsPlaceholder)
                return DisplayName;

            if (!string.IsNullOrWhiteSpace(MinimumVersion))
            {
                return $"{DisplayName} >= {MinimumVersion}";
            }

            return DisplayName;
        }
    }

    public string StatusPrefix
    {
        get
        {
            if (!IsRequired)
            {
                if (!IsInstalled)
                    return "[未安装] ";

                if (IsInstalledButDisabled)
                    return "[被禁用] ";

                return "[可选] ";
            }

            if (!IsInstalled)
                return "[未安装] ";

            if (IsInstalledButDisabled)
                return "[被禁用] ";

            return string.Empty;
        }
    }

    public string StatusPrefixColor
    {
        get
        {
            if (!IsRequired)
                return (!IsInstalled || IsInstalledButDisabled) ? "#D8A131" : "#3E8EDE";

            if (!IsInstalled || IsInstalledButDisabled)
                return "#D45555";

            return "#3E8EDE";
        }
    }

    public string StatusSuffix
    {
        get
        {
            if (!IsRequired && (!IsInstalled || IsInstalledButDisabled))
                return "[可选] ";

            return string.Empty;
        }
    }
}
