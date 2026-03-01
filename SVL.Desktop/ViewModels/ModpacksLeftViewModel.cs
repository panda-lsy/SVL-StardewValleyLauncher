using CommunityToolkit.Mvvm.ComponentModel;

namespace SVL.Desktop.ViewModels;

public partial class ModpacksLeftViewModel : ObservableObject
{
    private MainWindowViewModel _mainViewModel;

    public ModpacksLeftViewModel(MainWindowViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
    }
}
