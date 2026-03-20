using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using SVL.Core.Stardew.Mod;
using SVL.Desktop.ViewModels;

namespace SVL.Desktop.Views;

public partial class VersionSettingsContentView : UserControl
{
    public VersionSettingsContentView()
    {
        InitializeComponent();
    }

    private void ModRow_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
            return;

        if (HasInteractiveAncestor(source))
            return;

        if (sender is not FrameworkElement element || element.DataContext is not SdVMod mod)
            return;

        if (DataContext is not VersionSettingsRightViewModel viewModel)
            return;

        if (viewModel.ToggleModSelectionCommand?.CanExecute(mod) != true)
            return;

        viewModel.ToggleModSelectionCommand.Execute(mod);
        e.Handled = true;
    }

    private static bool HasInteractiveAncestor(DependencyObject source)
    {
        for (var current = source; current != null; current = GetParent(current))
        {
            if (current is ButtonBase || current is TextBox || current is ComboBox || current is ScrollBar)
                return true;

            if (current is FrameworkElement element && element.Name is "RowContainer" or "ChildRowContainer")
                return false;
        }

        return false;
    }

    private static DependencyObject? GetParent(DependencyObject current)
    {
        if (current is Visual || current is Visual3D)
            return VisualTreeHelper.GetParent(current);

        return LogicalTreeHelper.GetParent(current);
    }

    private void TagChip_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not ModTagPanelItem item)
            return;

        if (!item.IsCustomTag)
        {
            e.Handled = true;
        }
    }
}
