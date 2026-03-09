using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace SVL.Desktop.Controls;

public partial class DependencyEnableDialog : Window
{
    public IReadOnlyList<DependencyEnableOption> SelectedDependencies =>
        (DependencyGroupsItemsControl.ItemsSource as IEnumerable<DependencyEnableGroup> ?? [])
        .SelectMany(group => group.Dependencies ?? [])
        .Where(item => item.IsSelected)
        .GroupBy(item => item.ModId)
        .Select(group => group.First())
        .ToList();

    public DependencyEnableDialog(IEnumerable<DependencyEnableGroup> dependencyGroups, string description)
    {
        InitializeComponent();
        DescriptionTextBlock.Text = description;
        DependencyGroupsItemsControl.ItemsSource = dependencyGroups.ToList();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    public static IReadOnlyList<DependencyEnableOption>? Show(Window? owner, IEnumerable<DependencyEnableGroup> dependencyGroups, string description)
    {
        var dialog = new DependencyEnableDialog(dependencyGroups, description);
        if (owner != null)
        {
            dialog.Owner = owner;
        }

        return dialog.ShowDialog() == true ? dialog.SelectedDependencies : null;
    }
}

public sealed class DependencyEnableOption
{
    public string ModId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string UniqueId { get; set; } = string.Empty;

    public bool IsSelected { get; set; } = true;
}

public sealed class DependencyEnableGroup
{
    public string TargetModName { get; set; } = string.Empty;

    public IReadOnlyList<DependencyEnableOption> Dependencies { get; set; } = [];

    public string HeaderText => $"{TargetModName} ({Dependencies.Count})";
}