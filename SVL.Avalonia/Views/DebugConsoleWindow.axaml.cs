using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using SVL.Avalonia.Services;

namespace SVL.Avalonia.Views;

public partial class DebugConsoleWindow : Window
{
    private static readonly string[] SyncedResourceKeys =
    [
        "AccentBrush",
        "CardBrush",
        "BorderBrush",
        "SurfaceBrush",
        "WindowBackgroundBrush",
        "TextPrimaryBrush",
        "TextSecondaryBrush",
        "TextOnAccentBrush",
        "PillBg",
        "CategorySelectedBg",
        "CategoryIndicator"
    ];

    public DebugConsoleWindow()
    {
        InitializeComponent();
        RefreshThemeResources();
        ThemeService.ThemeChanged += OnThemeChanged;
        Closed += OnClosed;
    }

    private void OnThemeChanged()
    {
        Dispatcher.UIThread.Post(RefreshThemeResources);
    }

    private void RefreshThemeResources()
    {
        var appResources = Application.Current?.Resources;
        if (appResources == null)
        {
            return;
        }

        foreach (var key in SyncedResourceKeys)
        {
            if (appResources.TryGetValue(key, out var value))
            {
                Resources[key] = value;
            }
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        ThemeService.ThemeChanged -= OnThemeChanged;
        Closed -= OnClosed;
    }
}