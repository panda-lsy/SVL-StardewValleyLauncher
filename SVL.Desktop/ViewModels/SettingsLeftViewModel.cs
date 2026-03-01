using CommunityToolkit.Mvvm.ComponentModel;

namespace SVL.Desktop.ViewModels;

public partial class SettingsLeftViewModel : ObservableObject
{
    private MainWindowViewModel _mainViewModel;

    public SettingsLeftViewModel(MainWindowViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
    }
}
