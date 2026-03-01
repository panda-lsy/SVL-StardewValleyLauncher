using System.Windows;
using SVL.Desktop.ViewModels;

namespace SVL.Desktop.Views;

/// <summary>
/// DownloadManagerWindow.xaml 的交互逻辑
/// </summary>
public partial class DownloadManagerWindow : Window
{
    private readonly DownloadManagerViewModel _viewModel;

    public DownloadManagerWindow()
    {
        InitializeComponent();
        _viewModel = DownloadManagerViewModel.Instance;
        DataContext = _viewModel;
    }

    /// <summary>
    /// 显示窗口
    /// </summary>
    public void ShowWindow()
    {
        _viewModel.ShowWindow();
        Show();
    }

    /// <summary>
    /// 隐藏窗口
    /// </summary>
    public new void Hide()
    {
        _viewModel.HideWindow();
        base.Hide();
    }

    /// <summary>
    /// 切换显示状态
    /// </summary>
    public void Toggle()
    {
        if (IsVisible)
        {
            Hide();
        }
        else
        {
            ShowWindow();
        }
    }
}
