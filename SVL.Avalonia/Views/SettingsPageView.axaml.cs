using Avalonia.Controls;
using Avalonia.VisualTree;
using SVL.Avalonia.ViewModels;
using System.Diagnostics;

namespace SVL.Avalonia.Views;

public partial class SettingsPageView : UserControl
{
    public SettingsPageView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => EnsureSettingsDataContext();
    }

    private void EnsureSettingsDataContext()
    {
        if (DataContext is SettingsPageViewModel)
        {
            return;
        }

        // First try direct visual parent.
        var parentDataContext = this.GetVisualParent()?.DataContext;
        if (TryResolveSettingsDataContext(parentDataContext, out var directResolved))
        {
            DataContext = directResolved;
            Debug.WriteLine("[SettingsPageView] DataContext resolved from direct parent.");
            return;
        }

        // Fallback: walk up visual ancestors to locate SettingsPageViewModel or MainWindowViewModel.
        foreach (var ancestor in this.GetSelfAndVisualAncestors())
        {
            if (TryResolveSettingsDataContext(ancestor.DataContext, out var resolved))
            {
                DataContext = resolved;
                Debug.WriteLine("[SettingsPageView] DataContext resolved from visual ancestors.");
                return;
            }
        }

        Debug.WriteLine("[SettingsPageView] Unable to resolve SettingsPageViewModel DataContext.");
    }

    private static bool TryResolveSettingsDataContext(object? candidate, out SettingsPageViewModel? resolved)
    {
        if (candidate is SettingsPageViewModel settingsVm)
        {
            resolved = settingsVm;
            return true;
        }

        if (candidate is MainWindowViewModel mainVm && mainVm.SettingsPage != null)
        {
            resolved = mainVm.SettingsPage;
            return true;
        }

        resolved = null;
        return false;
    }
}
