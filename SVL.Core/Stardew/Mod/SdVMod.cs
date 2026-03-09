using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using SVL.Core.Stardew.Mod;

namespace SVL.Core.Stardew.Mod;

public class SdVMod : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _hasUpdate;
    private bool _isGroupExpanded;

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
    public bool IsBackupItem { get; set; }
    public DateTime? BackupTime { get; set; }
    public string BackupLabel { get; set; } = string.Empty;
    public string OriginalRelativePath { get; set; } = string.Empty;
    public bool IsChildMod { get; set; }
    public bool IsCompositeParent { get; set; }
    public string ParentModId { get; set; } = string.Empty;
    public string ParentModName { get; set; } = string.Empty;
    public ObservableCollection<SdVMod> ChildMods { get; } = [];
    public string TagsDisplay => Tags == null || Tags.Count == 0 ? string.Empty : string.Join(" / ", Tags);
    public bool HasChildren => ChildMods.Count > 0;
    public bool CanShowOnlineDetails => !IsChildMod;
    public string GroupToggleText => IsGroupExpanded ? "⌄" : "⌃";
    public string FolderName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ModPath))
                return string.Empty;

            var folderName = Path.GetFileName(ModPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (folderName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
            {
                folderName = folderName.Substring(0, folderName.Length - ".disabled".Length);
            }

            return folderName;
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

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
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

            var prefix = IsRequired ? string.Empty : "[可选] ";
            if (!string.IsNullOrWhiteSpace(MinimumVersion))
            {
                return $"{prefix}{DisplayName} >= {MinimumVersion}";
            }

            return $"{prefix}{DisplayName}";
        }
    }
}
