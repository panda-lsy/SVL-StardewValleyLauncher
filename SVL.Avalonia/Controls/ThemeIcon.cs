using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;

namespace SVL.Avalonia.Controls;

/// <summary>
/// Resolves semantic icon names to transparent pixel sprites from Stardew Valley.
/// Existing XAML bindings keep using the same semantic names.
/// </summary>
public sealed class ThemeIcon : Image
{
    private static readonly ConcurrentDictionary<string, Bitmap> BitmapCache = new(StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, string> GameIconAssets =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["retro-pc"] = "Stardew/Vanilla.png",
            ["pencil-ruler"] = "Modded.png",
            ["notebook"] = "Stardew/Book.png",
            ["archive"] = "Stardew/Chest.png",
            ["download"] = "Stardew/Download.png",
            ["download-alt"] = "Stardew/Download.png",
            ["file-import"] = "Stardew/Download.png",
            ["upload"] = "Stardew/Export.png",
            ["analytics"] = "Stardew/Details.png",
            ["cog"] = "Stardew/Settings.png",
            ["paint-brush"] = "Stardew/Brush.png",
            ["info-circle"] = "Stardew/Info.png",
            ["question-circle"] = "Stardew/Info.png",
            ["exclamation-triangle"] = "Stardew/Warning.png",
            ["lightbulb"] = "Stardew/Tip.png",
            ["plus"] = "Stardew/Add.png",
            ["refresh"] = "Stardew/Update.png",
            ["globe"] = "Stardew/Online.png",
            ["link"] = "Stardew/Online.png",
            ["search"] = "Stardew/Search.png",
            ["ellipses-horizontal"] = "Stardew/Details.png",
            ["bolt"] = "Stardew/Update.png",
            ["arrow-right"] = "Stardew/ArrowRight.png",
            ["arrow-left"] = "Stardew/ArrowLeft.png",
            ["star"] = "Stardew/Star.png",
            ["clock"] = "Stardew/Clock.png",
            ["finish"] = "Stardew/Finish.png",
            ["check-circle"] = "Stardew/Enable.png",
            ["times-circle"] = "Stardew/Close.png",
            ["times"] = "Stardew/Close.png",
            ["octagon-times"] = "Stardew/Close.png",
            ["lock-open"] = "Stardew/Key.png",
            ["lock"] = "Stardew/Key.png",
            ["logout"] = "Stardew/ArrowRight.png",
            ["folder-open"] = "Stardew/Catalog.png",
            ["save"] = "Stardew/Backup.png",
            ["trash"] = "Stardew/Trash.png",
            ["local-details"] = "Stardew/Details.png"
        };

    public static readonly StyledProperty<string> IconNameProperty =
        AvaloniaProperty.Register<ThemeIcon, string>(nameof(IconName), string.Empty);
    public static readonly StyledProperty<bool> UseLightGlyphProperty =
        AvaloniaProperty.Register<ThemeIcon, bool>(nameof(UseLightGlyph));

    public ThemeIcon()
    {
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
    }

    public string IconName
    {
        get => GetValue(IconNameProperty);
        set => SetValue(IconNameProperty, value);
    }

    /// <summary>Compatibility property for older XAML; game sprites are theme independent.</summary>
    public bool UseLightGlyph
    {
        get => GetValue(UseLightGlyphProperty);
        set => SetValue(UseLightGlyphProperty, value);
    }

    public static bool IsIconMapped(string? iconName)
    {
        return !string.IsNullOrWhiteSpace(iconName) && GameIconAssets.ContainsKey(iconName.Trim());
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IconNameProperty)
        {
            RefreshIcon();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RefreshIcon();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
    }

    private void RefreshIcon()
    {
        if (string.IsNullOrWhiteSpace(IconName))
        {
            Source = null;
            return;
        }

        var assetPath = GameIconAssets.TryGetValue(IconName.Trim(), out var gameAsset)
            ? gameAsset
            : "Stardew/Info.png";
        var assetUri = $"avares://SVL.Avalonia/Assets/Icons/{assetPath}";

        try
        {
            Source = BitmapCache.GetOrAdd(assetUri, static uri =>
            {
                using var stream = AssetLoader.Open(new Uri(uri));
                return new Bitmap(stream);
            });
        }
        catch (Exception exception)
        {
            Source = null;
            Debug.WriteLine($"Failed to load pixel icon '{assetUri}': {exception.Message}");
        }
    }
}
