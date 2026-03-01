using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SVL.Desktop.ViewModels;

public partial class ModpacksRightViewModel : ObservableObject
{
    private MainWindowViewModel _mainViewModel;

    public ModpacksRightViewModel(MainWindowViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
        _status = "Modpack 功能开发中";
        _description = "Modpack 管理功能即将推出，敬请期待！";
    }

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private string _description = "";

    [RelayCommand]
    private void CreateModpack()
    {
        _status = "创建 Modpack 功能开发中";
    }

    [RelayCommand]
    private void ImportModpack()
    {
        _status = "导入 Modpack 功能开发中";
    }
}
