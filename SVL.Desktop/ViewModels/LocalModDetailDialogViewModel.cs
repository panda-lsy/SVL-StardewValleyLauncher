using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SVL.Core.Logging;
using SVL.Core.Stardew.Localization;
using SVL.Core.Stardew.Mod;
using SVL.Desktop.Controls;

namespace SVL.Desktop.ViewModels;

public partial class LocalModDetailDialogViewModel : ObservableObject
{
    private readonly SdVMod _mod;
    private readonly Func<ModDependencyLink, Task>? _navigateDependencyAsync;

    [ObservableProperty]
    private string _modName = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string _version = string.Empty;

    [ObservableProperty]
    private string _author = string.Empty;

    [ObservableProperty]
    private string _uniqueId = string.Empty;

    [ObservableProperty]
    private string _sourceFileName = string.Empty;

    [ObservableProperty]
    private string _modPath = string.Empty;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private string _isEnabledBackground = "#D2A679";

    [ObservableProperty]
    private string _isEnabledText = "已启用";

    [ObservableProperty]
    private bool _hasUpdate;

    [ObservableProperty]
    private bool _canOpenFolder = true;

    [ObservableProperty]
    private bool _hasDependencies;

    [ObservableProperty]
    private bool _hasLocalization;

    [ObservableProperty]
    private string _localizationContributor = string.Empty;

    [ObservableProperty]
    private string _localizationUpdatedAt = string.Empty;

    public ObservableCollection<ModDependencyLink> Dependencies { get; } = new();

    public event EventHandler? RequestClose;

    public LocalModDetailDialogViewModel(SdVMod mod, Func<ModDependencyLink, Task>? navigateDependencyAsync = null)
    {
        _mod = mod ?? throw new ArgumentNullException(nameof(mod));
        _navigateDependencyAsync = navigateDependencyAsync;

        // 加载MOD信息
        LoadModInfo();

        // 异步加载本地化贡献者信息
        _ = LoadLocalizationContributorAsync();
    }

    private async Task LoadLocalizationContributorAsync()
    {
        try
        {
            var source = TryReadSourceMetadata();
            if (source?.Localization != null)
            {
                if (!string.IsNullOrWhiteSpace(source.Localization.Contributor))
                    LocalizationContributor = source.Localization.Contributor;

                if (!string.IsNullOrWhiteSpace(source.Localization.UpdatedAt))
                    LocalizationUpdatedAt = source.Localization.UpdatedAt;
            }

            CommunityLocalizationEntry? localization = null;
            var sourceInfo = TryGetLocalizationSourceInfo();
            if (sourceInfo != null)
            {
                localization = await CommunityLocalizationService.GetAsync("mod", sourceInfo.Value.Platform, sourceInfo.Value.ProjectId, forceRefresh: false);
            }
            else if (!string.IsNullOrWhiteSpace(_mod.UniqueId))
            {
                localization = await CommunityLocalizationService.GetByUniqueIdAsync(_mod.UniqueId, forceRefresh: false);
            }

            if (localization?.Meta != null)
            {
                if (!string.IsNullOrWhiteSpace(localization.Meta.Contributor))
                    LocalizationContributor = localization.Meta.Contributor;

                if (!string.IsNullOrWhiteSpace(localization.Meta.UpdatedAt))
                    LocalizationUpdatedAt = localization.Meta.UpdatedAt;
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"[LocalModDetailDialog] 加载本地化贡献者信息失败: {ex.Message}");
        }
    }

    private (string Platform, string ProjectId)? TryGetLocalizationSourceInfo()
    {
        // 优先使用来源凭证
        var credential = TryReadSourceCredential();
        if (credential.HasValue)
        {
            return credential;
        }

        // 其次使用UpdateKeys
        if (_mod.Manifest?.UpdateKeys?.Count > 0)
        {
            foreach (var updateKey in _mod.Manifest.UpdateKeys)
            {
                var parts = updateKey.Split(new[] { ':' }, 2);
                if (parts.Length == 2)
                {
                    var source = parts[0].ToLowerInvariant();
                    var identifier = parts[1].Trim();

                    if (source == "curseforge")
                    {
                        return ("Curseforge", identifier);
                    }
                    else if (source == "nexus")
                    {
                        return ("NexusMods", identifier);
                    }
                }
            }
        }

        // 最后使用CurseforgeProjectId或NexusModsProjectId
        if (!string.IsNullOrWhiteSpace(_mod.CurseforgeProjectId))
        {
            return ("Curseforge", _mod.CurseforgeProjectId);
        }
        else if (!string.IsNullOrWhiteSpace(_mod.NexusModsProjectId))
        {
            return ("NexusMods", _mod.NexusModsProjectId);
        }

        return null;
    }

    private void LoadModInfo()
    {
        ModName = _mod.DisplayName ?? _mod.Name ?? "未知MOD";
        Description = _mod.DisplayDescription ?? _mod.Description ?? "无描述";
        Version = _mod.Version ?? "未知版本";
        Author = _mod.Author ?? "未知作者";
        UniqueId = _mod.UniqueId ?? "无";
        SourceFileName = !string.IsNullOrEmpty(_mod.SourceFileName) ? _mod.SourceFileName : "无";
        ModPath = _mod.ModPath ?? "无";
        IsEnabled = _mod.IsEnabled;
        HasUpdate = _mod.HasUpdate;
        HasLocalization = _mod.HasLocalizedName || _mod.HasLocalizedDescription;

        // 更新启用状态显示
        UpdateEnabledStatus();

        // 加载依赖项
        if (_mod.DisplayDependencies != null && _mod.DisplayDependencies.Count > 0)
        {
            HasDependencies = true;
            foreach (var dep in _mod.DisplayDependencies)
            {
                Dependencies.Add(dep);
            }
        }
        else if (_mod.DependencyDetails != null && _mod.DependencyDetails.Count > 0)
        {
            HasDependencies = true;
            foreach (var dep in _mod.DependencyDetails)
            {
                Dependencies.Add(new ModDependencyLink
                {
                    UniqueId = dep.UniqueId ?? string.Empty,
                    DisplayName = dep.UniqueId ?? string.Empty,
                    MinimumVersion = dep.MinimumVersion ?? string.Empty,
                    IsRequired = dep.IsRequired
                });
            }
        }
        else
        {
            HasDependencies = false;
        }

        // 检查是否可以打开文件夹
        CanOpenFolder = !string.IsNullOrEmpty(_mod.ModPath) && System.IO.Directory.Exists(_mod.ModPath);
    }

    private void UpdateEnabledStatus()
    {
        if (IsEnabled)
        {
            IsEnabledBackground = "#D2A679";
            IsEnabledText = "已启用";
        }
        else
        {
            IsEnabledBackground = "#9E9E9E";
            IsEnabledText = "已禁用";
        }
    }

    [RelayCommand]
    private void Close()
    {
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (!string.IsNullOrEmpty(_mod.ModPath) && System.IO.Directory.Exists(_mod.ModPath))
        {
            try
            {
                Process.Start("explorer.exe", _mod.ModPath);
            }
            catch (Exception ex)
            {
                SvlMessageBox.Error($"无法打开文件夹：{ex.Message}");
            }
        }
    }

    [RelayCommand]
    private void OpenDependency(ModDependencyLink dependency)
    {
        if (dependency == null || _navigateDependencyAsync == null)
        {
            return;
        }

        RequestClose?.Invoke(this, EventArgs.Empty);

        Application.Current?.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                await _navigateDependencyAsync(dependency);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[LocalModDetailDialog] 跳转前置 Mod 失败");
                SvlMessageBox.Error($"无法打开前置 Mod：{ex.Message}");
            }
        }));
    }

    [RelayCommand]
    private void ShowLocalizedText()
    {
        _mod.SetLocalizationLanguage(true);
        LoadModInfo();
    }

    [RelayCommand]
    private void ShowSourceText()
    {
        _mod.SetLocalizationLanguage(false);
        LoadModInfo();
    }

    [RelayCommand]
    private void OpenLocalizationContribution()
    {
        try
        {
            // 尝试获取在线详情对应的信息
            string? platform = null;
            string? projectId = null;

            // 优先使用来源凭证
            var credential = TryReadSourceCredential();
            if (credential.HasValue)
            {
                platform = credential.Value.Platform;
                projectId = credential.Value.ProjectId;
            }
            // 其次使用UpdateKeys
            else if (_mod.Manifest?.UpdateKeys?.Count > 0)
            {
                foreach (var updateKey in _mod.Manifest.UpdateKeys)
                {
                    var parts = updateKey.Split(new[] { ':' }, 2);
                    if (parts.Length == 2)
                    {
                        var source = parts[0].ToLowerInvariant();
                        var identifier = parts[1].Trim();

                        if (source == "curseforge" || source == "nexus")
                        {
                            platform = source == "curseforge" ? "Curseforge" : "NexusMods";
                            projectId = identifier;
                            break;
                        }
                    }
                }
            }
            // 最后使用CurseforgeProjectId或NexusModsProjectId
            else if (!string.IsNullOrWhiteSpace(_mod.CurseforgeProjectId))
            {
                platform = "Curseforge";
                projectId = _mod.CurseforgeProjectId;
            }
            else if (!string.IsNullOrWhiteSpace(_mod.NexusModsProjectId))
            {
                platform = "NexusMods";
                projectId = _mod.NexusModsProjectId;
            }

            string url;
            if (!string.IsNullOrWhiteSpace(platform) && !string.IsNullOrWhiteSpace(projectId))
            {
                // 有平台信息，使用id参数
                var idParam = platform.ToLowerInvariant() == "curseforge" ? $"curse-{projectId}" : $"nexus-{projectId}";
                url = $"https://svl-website.89b52195.er.aliyun-esa.net/contribute?id={idParam}&auto=1";
            }
            else if (!string.IsNullOrWhiteSpace(_mod.UniqueId))
            {
                // 没有平台信息但有UniqueID，使用UniqueID模式
                var rawTitle = Uri.EscapeDataString(_mod.Name ?? "");
                var rawDescription = Uri.EscapeDataString(_mod.Description ?? "");
                url = $"https://svl-website.89b52195.er.aliyun-esa.net/contribute?uniqueid={_mod.UniqueId}&rawtitle={rawTitle}&rawdescription={rawDescription}&auto=1";
            }
            else
            {
                // 没有任何信息，只打开贡献页面
                url = "https://svl-website.89b52195.er.aliyun-esa.net/contribute";
            }

            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[LocalModDetailDialog] 打开本地化贡献页面失败");
            SvlMessageBox.Error($"无法打开贡献页面：{ex.Message}");
        }
    }

    private SvlSourceMetadata? TryReadSourceMetadata()
    {
        if (string.IsNullOrWhiteSpace(_mod.ModPath))
            return null;

        return SvlSourceMetadataStore.TryReadFromDirectory(_mod.ModPath);
    }

    private (string Platform, string ProjectId)? TryReadSourceCredential()
    {
        var credential = TryReadSourceMetadata();
        if (credential == null || string.IsNullOrWhiteSpace(credential.Platform) || string.IsNullOrWhiteSpace(credential.ProjectId))
            return null;

        return (credential.Platform, credential.ProjectId);
    }

    [RelayCommand]
    private void ShowLocalizationContributorInfo()
    {
        var message = "贡献本地化说明\n\n" +
            "点击「贡献本地化」按钮可以跳转到社区本地化贡献页面，为当前 Mod 添加中文翻译。\n\n";

        if (!string.IsNullOrWhiteSpace(LocalizationContributor))
        {
            message += $"当前 Mod 的本地化贡献者：{LocalizationContributor}";
        }
        else
        {
            message += "当前 Mod 还没有人进行汉化贡献，欢迎前往贡献页面参与补充。";
        }

        if (!string.IsNullOrWhiteSpace(LocalizationUpdatedAt))
        {
            message += $"\n\n本地化最终更新时间：{LocalizationUpdatedAt}";
        }

        SvlMessageBox.Info(message, "贡献本地化");
    }
}
