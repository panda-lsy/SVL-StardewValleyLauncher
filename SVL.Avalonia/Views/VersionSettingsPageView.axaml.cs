using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using SVL.Avalonia.ViewModels;

namespace SVL.Avalonia.Views;

public partial class VersionSettingsPageView : UserControl
{
    public VersionSettingsPageView()
    {
        InitializeComponent();
    }

    private void OnTagChipPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ToggleButton { DataContext: ModTagPanelItem item } tagChip)
        {
            return;
        }

        var point = e.GetCurrentPoint(tagChip);
        if (!point.Properties.IsRightButtonPressed)
        {
            return;
        }

        // 仅自定义标签允许弹出右键菜单，避免普通标签出现无效菜单。
        if (!item.IsCustomTag)
        {
            e.Handled = true;
        }
    }

    private static ModTagPanelItem? ResolveTagItemFromMenuSender(object? sender)
    {
        if (sender is not MenuItem menuItem)
        {
            return null;
        }

        if (menuItem.Tag is ModTagPanelItem fromTag)
        {
            return fromTag;
        }

        if (menuItem.DataContext is ModTagPanelItem fromDataContext)
        {
            return fromDataContext;
        }

        if (menuItem.Parent is ContextMenu { PlacementTarget: Control { DataContext: ModTagPanelItem fromPlacement } })
        {
            return fromPlacement;
        }

        return null;
    }

    private void OnCustomTagRenameClicked(object? sender, RoutedEventArgs e)
    {
        var item = ResolveTagItemFromMenuSender(sender);
        if (item == null || !item.IsCustomTag)
        {
            return;
        }

        if (DataContext is not VersionSettingsPageViewModel viewModel)
        {
            return;
        }

        if (viewModel.RenameCustomTagFromChipCommand.CanExecute(item))
        {
            viewModel.RenameCustomTagFromChipCommand.Execute(item);
        }
    }

    private void OnCustomTagDeleteClicked(object? sender, RoutedEventArgs e)
    {
        var item = ResolveTagItemFromMenuSender(sender);
        if (item == null || !item.IsCustomTag)
        {
            return;
        }

        if (DataContext is not VersionSettingsPageViewModel viewModel)
        {
            return;
        }

        if (viewModel.DeleteCustomTagFromChipCommand.CanExecute(item))
        {
            viewModel.DeleteCustomTagFromChipCommand.Execute(item);
        }
    }
}
