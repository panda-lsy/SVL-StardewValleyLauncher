using System.Windows.Controls;
using SVL.Desktop.ViewModels;

namespace SVL.Desktop.Controls;

/// <summary>
/// DownloadFloatingButton.xaml 的交互逻辑
/// </summary>
public partial class DownloadFloatingButton : UserControl
{
    private readonly System.Windows.Threading.DispatcherTimer _closeButtonHoverTimer;

    public bool SuppressNextClick { get; set; }

    public DownloadFloatingButton()
    {
        InitializeComponent();
        DataContext = DownloadManagerViewModel.Instance;

        _closeButtonHoverTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = System.TimeSpan.FromMilliseconds(120)
        };
        _closeButtonHoverTimer.Tick += (_, _) =>
        {
            _closeButtonHoverTimer.Stop();
            if (RootGrid.IsMouseOver)
            {
                CloseButton.Visibility = System.Windows.Visibility.Visible;
            }
        };
    }

    private void FloatingButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (SuppressNextClick)
        {
            SuppressNextClick = false;
            e.Handled = true;
            return;
        }

        if (DataContext is DownloadManagerViewModel viewModel && viewModel.ToggleCommand.CanExecute(null))
        {
            viewModel.ToggleCommand.Execute(null);
        }
    }

    private void CloseButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        SuppressNextClick = true;
        e.Handled = true;

        if (DataContext is DownloadManagerViewModel viewModel && viewModel.HideFloatingButtonCommand.CanExecute(null))
        {
            viewModel.HideFloatingButtonCommand.Execute(null);
        }
    }

    private void RootGrid_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _closeButtonHoverTimer.Stop();
        _closeButtonHoverTimer.Start();
    }

    private void RootGrid_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _closeButtonHoverTimer.Stop();
        CloseButton.Visibility = System.Windows.Visibility.Collapsed;
    }
}
