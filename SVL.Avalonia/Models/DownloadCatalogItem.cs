using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace SVL.Avalonia.Models;

public partial class DownloadCatalogItem : ObservableObject
{
    [ObservableProperty]
    private string _displayText = string.Empty;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _sourceTag = string.Empty;

    [ObservableProperty]
    private string _sourceKey = string.Empty;

    [ObservableProperty]
    private string _stat = string.Empty;

    [ObservableProperty]
    private string _metricTag = string.Empty;

    [ObservableProperty]
    private string _timeTag = string.Empty;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private string _sourceName = string.Empty;

    [ObservableProperty]
    private string _sourceSummary = string.Empty;

    [ObservableProperty]
    private string _localizedName = string.Empty;

    [ObservableProperty]
    private string _localizedSummary = string.Empty;

    [ObservableProperty]
    private bool _useLocalizedText;

    [ObservableProperty]
    private bool _useLocalizedName = true;

    [ObservableProperty]
    private bool _useLocalizedSummary = true;

    [ObservableProperty]
    private string _modTypeTag = string.Empty;

    [ObservableProperty]
    private string _gameVersionTag = string.Empty;

    [ObservableProperty]
    private string _iconSource = string.Empty;

    [ObservableProperty]
    private string _fullIconSource = string.Empty;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isLoadingDetails;

    [ObservableProperty]
    private bool _hasLoadedDetails;

    public ObservableCollection<string> VersionOptions { get; } = [];

    public ObservableCollection<string> DependencyOptions { get; } = [];

    public ObservableCollection<string> DownloadOptions { get; } = [];

    public bool HasVersionOptions => VersionOptions.Count > 0;

    public bool HasDependencyOptions => DependencyOptions.Count > 0;

    public bool HasDownloadOptions => DownloadOptions.Count > 0;

    public bool HasMetricTag => !string.IsNullOrWhiteSpace(MetricTag);

    public bool HasTimeTag => !string.IsNullOrWhiteSpace(TimeTag);

    public bool HasModTypeTag => !string.IsNullOrWhiteSpace(ModTypeTag);

    public bool HasGameVersionTag => !string.IsNullOrWhiteSpace(GameVersionTag);

    public bool HasLocalizedName => !string.IsNullOrWhiteSpace(LocalizedName);

    public bool HasLocalizedSummary => !string.IsNullOrWhiteSpace(LocalizedSummary);

    public string DisplayName
    {
        get
        {
            if (UseLocalizedName && !string.IsNullOrWhiteSpace(LocalizedName))
            {
                return LocalizedName;
            }

            if (!string.IsNullOrWhiteSpace(SourceName))
            {
                return SourceName;
            }

            return Name;
        }
    }

    public string DisplaySummary
    {
        get
        {
            if (UseLocalizedSummary && !string.IsNullOrWhiteSpace(LocalizedSummary))
            {
                return LocalizedSummary;
            }

            if (!string.IsNullOrWhiteSpace(SourceSummary))
            {
                return SourceSummary;
            }

            return Summary;
        }
    }

    public string LocalizationToggleButtonText =>
        (UseLocalizedName && UseLocalizedSummary) ? "EN" : "中";

    public string ExpandButtonText => IsExpanded ? "收起" : "展开";

    public DownloadCatalogItem()
    {
        VersionOptions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasVersionOptions));
        DependencyOptions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasDependencyOptions));
        DownloadOptions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasDownloadOptions));
    }

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ExpandButtonText));
    }

    partial void OnMetricTagChanged(string value)
    {
        OnPropertyChanged(nameof(HasMetricTag));
    }

    partial void OnTimeTagChanged(string value)
    {
        OnPropertyChanged(nameof(HasTimeTag));
    }

    partial void OnModTypeTagChanged(string value)
    {
        OnPropertyChanged(nameof(HasModTypeTag));
    }

    partial void OnGameVersionTagChanged(string value)
    {
        OnPropertyChanged(nameof(HasGameVersionTag));
    }

    partial void OnUseLocalizedTextChanged(bool value)
    {
        UseLocalizedName = value;
        UseLocalizedSummary = value;
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(DisplaySummary));
    }

    partial void OnUseLocalizedNameChanged(bool value)
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(LocalizationToggleButtonText));
    }

    partial void OnUseLocalizedSummaryChanged(bool value)
    {
        OnPropertyChanged(nameof(DisplaySummary));
        OnPropertyChanged(nameof(LocalizationToggleButtonText));
    }

    partial void OnSourceNameChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayName));
    }

    partial void OnSourceSummaryChanged(string value)
    {
        OnPropertyChanged(nameof(DisplaySummary));
    }

    partial void OnLocalizedNameChanged(string value)
    {
        OnPropertyChanged(nameof(HasLocalizedName));
        OnPropertyChanged(nameof(DisplayName));
    }

    partial void OnLocalizedSummaryChanged(string value)
    {
        OnPropertyChanged(nameof(HasLocalizedSummary));
        OnPropertyChanged(nameof(DisplaySummary));
    }
}
