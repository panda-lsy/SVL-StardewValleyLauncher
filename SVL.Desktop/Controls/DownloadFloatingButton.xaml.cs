using System.Windows.Controls;
using SVL.Desktop.ViewModels;

namespace SVL.Desktop.Controls;

/// <summary>
/// DownloadFloatingButton.xaml 的交互逻辑
/// </summary>
public partial class DownloadFloatingButton : UserControl
{
    public DownloadFloatingButton()
    {
        InitializeComponent();
        DataContext = DownloadManagerViewModel.Instance;
    }
}
