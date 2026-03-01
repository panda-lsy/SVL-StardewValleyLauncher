using CommunityToolkit.Mvvm.ComponentModel;
using SVL.Core.Stardew.Instance;

namespace SVL.Desktop.ViewModels;

public partial class LaunchRightViewModel : ObservableObject
{
    private MainWindowViewModel _mainViewModel;

    public LaunchRightViewModel(MainWindowViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
        LoadInstances();
    }

    private void LoadInstances()
    {
        // 从配置加载实例
        var instances = SettingsService.LoadInstances();

        if (instances.Count == 0)
        {
            HasInstances = false;
            InstanceName = null;
            GameVersion = null;
            VersionStatus = null;
            HasSMAPI = false;
        }
        else
        {
            HasInstances = true;
            var firstInstance = instances[0];
            InstanceName = firstInstance.Name;
            GameVersion = firstInstance.Version;
            VersionStatus = firstInstance.IsSMAPIInstance ? $"模组版 {firstInstance.SMAPIVersion}" : "原版";
            HasSMAPI = firstInstance.IsSMAPIInstance;
        }
    }

    [ObservableProperty]
    private bool _hasInstances;

    [ObservableProperty]
    private string? _instanceName;

    [ObservableProperty]
    private string? _gameVersion;

    [ObservableProperty]
    private string? _versionStatus;

    [ObservableProperty]
    private bool _hasSMAPI;
}
