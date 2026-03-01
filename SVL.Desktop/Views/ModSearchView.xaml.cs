using System.Windows.Controls;
using SVL.Core.Logging;
using SVL.Desktop.ViewModels;

namespace SVL.Desktop.Views;

/// <summary>
/// ModSearchView.xaml 的交互逻辑
/// </summary>
public partial class ModSearchView : UserControl
{
    public ModSearchView()
    {
        InitializeComponent();
        Loaded += ModSearchView_Loaded;
    }

    private async void ModSearchView_Loaded(object sender, System.Windows.RoutedEventArgs e)
    {
        // 页面加载时初始化热门模组
        Log.Info("[ModSearchView] ModSearchView_Loaded 触发");

        if (DataContext is ModSearchViewModel viewModel)
        {
            Log.Info("[ModSearchView] DataContext 是 ModSearchViewModel，开始初始化");
            await viewModel.InitializeAsync();
        }
        else
        {
            Log.Warn($"[ModSearchView] DataContext 不是 ModSearchViewModel，实际类型: {DataContext?.GetType().Name}");
        }
    }

}
