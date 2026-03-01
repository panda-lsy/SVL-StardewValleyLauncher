using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using SVL.Core.Stardew.Mod;

namespace SVL.Core.Stardew.Mod;

public class SdVMod : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _hasUpdate;

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

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
