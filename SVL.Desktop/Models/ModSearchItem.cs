using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SVL.Core.IO;
using System.Windows;

namespace SVL.Desktop.Models;

/// <summary>
/// MOD 搜索项数据模型
/// </summary>
public partial class ModSearchItem : ObservableObject
{
    [ObservableProperty]
    private string _id = "";  // 唯一标识（nexus-{id} 或 curse-{id}）

    [ObservableProperty]
    private string _name = "";  // MOD 名称

    [ObservableProperty]
    private string _summary = "";  // MOD 简短描述

    [ObservableProperty]
    private string _description = "";  // MOD 完整描述

    [ObservableProperty]
    private string _iconUrl = "";  // MOD 图标 URL

    [ObservableProperty]
    private string _localIconPath = "";  // 本地缓存图标路径

    [ObservableProperty]
    private string _author = "";  // 作者

    [ObservableProperty]
    private long _downloadCount = 0;  // 下载量

    [ObservableProperty]
    private string _lastUpdateTime = "";  // 最后更新时间

    [ObservableProperty]
    private string _source = "";  // 来源：Curseforge 或 NexusMods

    [ObservableProperty]
    private string _category = "";  // 类型/分类

    [ObservableProperty]
    private List<string> _supportedGameVersions = new();  // 支持的星露谷版本列表

    [ObservableProperty]
    private double _rating = 0;  // 评分（0-5）

    [ObservableProperty]
    private string _url = "";  // 详情页面 URL

    [ObservableProperty]
    private string _localizedNameZhCn = "";

    [ObservableProperty]
    private string _localizedNameSource = "";

    [ObservableProperty]
    private string _localizedDescriptionZhCn = "";

    [ObservableProperty]
    private string _localizedDescriptionSource = "";

    [ObservableProperty]
    private string _localizationContributor = "";

    [ObservableProperty]
    private string _localizationUpdatedAt = "";

    [ObservableProperty]
    private bool _useLocalizedName = true;

    [ObservableProperty]
    private bool _useLocalizedDescription = true;

    public string DisplayName
    {
        get
        {
            if (UseLocalizedName && !string.IsNullOrWhiteSpace(LocalizedNameZhCn))
                return LocalizedNameZhCn;

            if (!string.IsNullOrWhiteSpace(LocalizedNameSource))
                return LocalizedNameSource;

            return Name;
        }
    }

    public string DisplaySummary
    {
        get
        {
            if (UseLocalizedDescription && !string.IsNullOrWhiteSpace(LocalizedDescriptionZhCn))
                return LocalizedDescriptionZhCn;

            if (!string.IsNullOrWhiteSpace(LocalizedDescriptionSource))
                return LocalizedDescriptionSource;

            if (!string.IsNullOrWhiteSpace(Summary))
                return Summary;

            return Description;
        }
    }

    public string DisplayDescription
    {
        get
        {
            if (UseLocalizedDescription && !string.IsNullOrWhiteSpace(LocalizedDescriptionZhCn))
                return LocalizedDescriptionZhCn;

            if (!string.IsNullOrWhiteSpace(LocalizedDescriptionSource))
                return LocalizedDescriptionSource;

            if (!string.IsNullOrWhiteSpace(Description))
                return Description;

            return Summary;
        }
    }

    public bool HasLocalizedName => !string.IsNullOrWhiteSpace(LocalizedNameZhCn);

    public bool HasLocalizedDescription => !string.IsNullOrWhiteSpace(LocalizedDescriptionZhCn);

    public bool HasAnyDisplayDescription => !string.IsNullOrWhiteSpace(DisplaySummary) || !string.IsNullOrWhiteSpace(DisplayDescription);

    partial void OnNameChanged(string value)
    {
        OnDisplayTextChanged();
    }

    partial void OnSummaryChanged(string value)
    {
        OnDisplayTextChanged();
    }

    partial void OnDescriptionChanged(string value)
    {
        OnDisplayTextChanged();
    }

    partial void OnLocalizedNameZhCnChanged(string value)
    {
        OnDisplayTextChanged();
    }

    partial void OnLocalizedNameSourceChanged(string value)
    {
        OnDisplayTextChanged();
    }

    partial void OnLocalizedDescriptionZhCnChanged(string value)
    {
        OnDisplayTextChanged();
    }

    partial void OnLocalizedDescriptionSourceChanged(string value)
    {
        OnDisplayTextChanged();
    }

    partial void OnUseLocalizedNameChanged(bool value)
    {
        OnPropertyChanged(nameof(DisplayName));
    }

    partial void OnUseLocalizedDescriptionChanged(bool value)
    {
        OnPropertyChanged(nameof(DisplaySummary));
        OnPropertyChanged(nameof(DisplayDescription));
        OnPropertyChanged(nameof(HasAnyDisplayDescription));
    }

    public void ApplyLocalization(string? localizedNameZhCn, string? localizedNameSource, string? localizedDescriptionZhCn, string? localizedDescriptionSource, string? localizationContributor = null, string? localizationUpdatedAt = null)
    {
        LocalizedNameZhCn = localizedNameZhCn ?? string.Empty;
        LocalizedNameSource = localizedNameSource ?? string.Empty;
        LocalizedDescriptionZhCn = localizedDescriptionZhCn ?? string.Empty;
        LocalizedDescriptionSource = localizedDescriptionSource ?? string.Empty;
        LocalizationContributor = localizationContributor ?? string.Empty;
        LocalizationUpdatedAt = localizationUpdatedAt ?? string.Empty;

        if (HasLocalizedName)
            UseLocalizedName = true;

        if (HasLocalizedDescription)
            UseLocalizedDescription = true;

        OnDisplayTextChanged();
    }

    [RelayCommand]
    private void ShowLocalizedName()
    {
        if (HasLocalizedName)
            UseLocalizedName = true;
    }

    [RelayCommand]
    private void ShowSourceName()
    {
        UseLocalizedName = false;
    }

    [RelayCommand]
    private void ShowLocalizedDescription()
    {
        if (HasLocalizedDescription)
            UseLocalizedDescription = true;
    }

    [RelayCommand]
    private void ShowSourceDescription()
    {
        UseLocalizedDescription = false;
    }

    private void OnDisplayTextChanged()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(DisplaySummary));
        OnPropertyChanged(nameof(DisplayDescription));
        OnPropertyChanged(nameof(HasLocalizedName));
        OnPropertyChanged(nameof(HasLocalizedDescription));
        OnPropertyChanged(nameof(HasAnyDisplayDescription));
    }

    /// <summary>
    /// 异步加载并缓存图标
    /// </summary>
    public async Task LoadIconAsync()
    {
        if (string.IsNullOrWhiteSpace(IconUrl))
            return;

        // 检查缓存
        var cachedPath = ImageCacheService.GetCachedImagePath(IconUrl);
        if (cachedPath != null)
        {
            // 在 UI 线程上更新属性（使用高优先级确保立即更新）
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                LocalIconPath = cachedPath;
            }, System.Windows.Threading.DispatcherPriority.Render);
            return;
        }

        // 下载并缓存
        var downloadedPath = await ImageCacheService.DownloadAndCacheImageAsync(IconUrl);
        if (downloadedPath != null)
        {
            // 在 UI 线程上更新属性（使用高优先级确保立即更新）
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                LocalIconPath = downloadedPath;
            }, System.Windows.Threading.DispatcherPriority.Render);
        }
    }
}
