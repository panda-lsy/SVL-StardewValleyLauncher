using CommunityToolkit.Mvvm.ComponentModel;

namespace SVL.Desktop.ViewModels;

public partial class SettingsRightViewModel : ObservableObject
{
    private MainWindowViewModel _mainViewModel;

    public SettingsRightViewModel(MainWindowViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
    }

    [ObservableProperty]
    private string _status = "设置";
}
