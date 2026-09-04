using Avalonia.Controls;
using Avalonia.Input;
using SVL.Avalonia.ViewModels;

namespace SVL.Avalonia.Views;

public partial class InstancesPageView : UserControl
{
    public InstancesPageView()
    {
        InitializeComponent();
    }

    private void PathEntry_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control)
        {
            return;
        }

        var point = e.GetCurrentPoint(control);
        var isRightClick = point.Properties.IsRightButtonPressed;
        var isMacCtrlClick = OperatingSystem.IsMacOS() &&
                             point.Properties.IsLeftButtonPressed &&
                             e.KeyModifiers.HasFlag(KeyModifiers.Control);

        if (!isRightClick && !isMacCtrlClick)
        {
            return;
        }

        if (DataContext is InstancesPageViewModel vm && control.DataContext is PathEntryItem item)
        {
            vm.SelectedPathEntry = item;
        }

        var menu = control.ContextMenu;
        if (menu == null)
        {
            return;
        }

        menu.Open(control);
        e.Handled = true;
    }
}
