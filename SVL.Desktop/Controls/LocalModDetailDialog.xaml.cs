using System;
using System.Windows;
using SVL.Core.Stardew.Mod;
using SVL.Desktop.ViewModels;

namespace SVL.Desktop.Controls;

public partial class LocalModDetailDialog : Window
{
    private readonly LocalModDetailDialogViewModel _viewModel;

    public LocalModDetailDialog(SdVMod mod, Func<ModDependencyLink, System.Threading.Tasks.Task>? navigateDependencyAsync = null)
    {
        InitializeComponent();
        _viewModel = new LocalModDetailDialogViewModel(mod, navigateDependencyAsync);
        DataContext = _viewModel;

        // 监听关闭事件
        _viewModel.RequestClose += (s, e) => Close();
    }

    /// <summary>
    /// 显示本地MOD详情对话框
    /// </summary>
    public static void Show(Window owner, SdVMod mod, Func<ModDependencyLink, System.Threading.Tasks.Task>? navigateDependencyAsync = null)
    {
        var dialog = new LocalModDetailDialog(mod, navigateDependencyAsync)
        {
            Owner = owner
        };

        // 应用模糊效果到父窗口
        if (owner is MainWindow mainWindow)
        {
            mainWindow.ApplyBlurEffect();
        }

        dialog.ShowDialog();

        // 移除模糊效果
        if (owner is MainWindow main)
        {
            main.RemoveBlurEffect();
        }
    }
}
