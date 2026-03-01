using System.Windows;
using SVL.Desktop.ViewModels;

namespace SVL.Desktop.Controls;

public partial class NexusLoginDialog : Window
{
    private readonly NexusLoginDialogViewModel _viewModel;

    public NexusLoginDialog()
    {
        InitializeComponent();
        _viewModel = new NexusLoginDialogViewModel();
        DataContext = _viewModel;

        // 监听关闭事件
        _viewModel.RequestClose += (s, e) => Close();
    }

    /// <summary>
    /// 显示登录对话框
    /// </summary>
    public async void Show(Window owner)
    {
        if (owner != null)
            Owner = owner;

        // 应用模糊效果到父窗口
        if (owner is MainWindow mainWindow)
        {
            mainWindow.ApplyBlurEffect();
        }

        // 异步初始化 ViewModel
        if (_viewModel is ViewModels.NexusLoginDialogViewModel loginViewModel)
        {
            await loginViewModel.InitializeAsync();
        }

        ShowDialog();

        // 移除模糊效果
        if (owner is MainWindow main)
        {
            main.RemoveBlurEffect();
        }
    }
}
