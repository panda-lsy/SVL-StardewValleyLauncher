using System.Windows;
using SVL.Desktop.ViewModels;

namespace SVL.Desktop.Controls;

public partial class ModpackDropDialog : Window
{
    private readonly ModpackDropDialogViewModel _viewModel;

    public ModpackDropDialog(string filePath)
    {
        InitializeComponent();
        _viewModel = new ModpackDropDialogViewModel();
        DataContext = _viewModel;

        // 设置 OwnerWindow 以便弹出子对话框时使用
        _viewModel.OwnerWindow = this;

        _viewModel.RequestClose += (s, e) =>
        {
            DialogResult = _viewModel.IsValid;
            Close();
        };

        // 加载文件
        _viewModel.LoadFromFileAsync(filePath);
    }

    /// <summary>
    /// 获取检测结果
    /// </summary>
    public ModpackDropDialogViewModel ViewModel => _viewModel;

    /// <summary>
    /// 显示对话框并返回结果
    /// </summary>
    public static ModpackDropDialogViewModel? Show(Window owner, string filePath)
    {
        var dialog = new ModpackDropDialog(filePath)
        {
            Owner = owner
        };

        var result = dialog.ShowDialog();
        if (result == true && dialog.ViewModel.IsValid)
        {
            return dialog.ViewModel;
        }

        return null;
    }
}
