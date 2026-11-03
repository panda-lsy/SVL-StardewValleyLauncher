using Avalonia.Controls;
using Avalonia;
using Avalonia.Threading;
using SVL.Avalonia.Models;
using SVL.Avalonia.ViewModels;

namespace SVL.Avalonia.Controls;

public partial class NexusLoginDialog : Window
{
    public NexusLoginDialog()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is NexusLoginDialogViewModel viewModel)
        {
            viewModel.RequestClose -= HandleRequestClose;
            viewModel.RequestClose += HandleRequestClose;
            _ = viewModel.InitializeAsync();
        }
    }

    private void HandleRequestClose(object? sender, NexusLoginResult? result)
    {
        // 确保在 UI 线程上关闭窗口。
        // 授权流程的续接可能不在 UI 线程上，直接调用 Close 会引发崩溃。
        Dispatcher.UIThread.Post(() => Close(result));
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is NexusLoginDialogViewModel viewModel)
        {
            viewModel.CancelPendingOAuth();
            viewModel.RequestClose -= HandleRequestClose;
        }

        base.OnClosed(e);
    }
}
