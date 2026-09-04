using System.Collections.ObjectModel;

namespace SVL.Avalonia.Services;

public sealed class ImageResourceService
{
    private const string DefaultTheme = "stardew-classic";
    private const string NeutralLanguage = "neutral";

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> Catalog =
        new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["stardew-classic"] = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                [NeutralLanguage] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["header.brand.junimo"] = "avares://SVL.Avalonia/Assets/Icons/Junimo.png",
                    ["launch.header.junimo"] = "avares://SVL.Avalonia/Assets/Icons/Junimo2.png",
                    ["launch.instance.none"] = "avares://SVL.Avalonia/Assets/Icons/Junimo.png",
                    ["launch.instance.modded"] = "avares://SVL.Avalonia/Assets/Icons/Modded.png",
                    ["launch.instance.vanilla"] = "avares://SVL.Avalonia/Assets/Icons/Vanilla.png",
                    ["download.category.smapi"] = "avares://SVL.Avalonia/Assets/Icons/Modded.png",
                    ["download.category.mods"] = "avares://SVL.Avalonia/Assets/Icons/Junimo.png",
                    ["download.category.modpacks"] = "avares://SVL.Avalonia/Assets/Icons/icon.png",
                    ["download.task.running"] = "avares://SVL.Avalonia/Assets/Icons/Junimo2.png",
                    ["download.task.completed"] = "avares://SVL.Avalonia/Assets/Icons/Vanilla.png",
                    ["download.task.failed"] = "avares://SVL.Avalonia/Assets/Icons/Modded.png",
                    ["download.task.pending"] = "avares://SVL.Avalonia/Assets/Icons/Junimo.png",
                    ["nav.launch"] = "avares://SVL.Avalonia/Assets/Icons/Vanilla.png",
                    ["nav.download"] = "avares://SVL.Avalonia/Assets/Icons/Junimo.png",
                    ["nav.tasks"] = "avares://SVL.Avalonia/Assets/Icons/Modded.png",
                    ["nav.settings"] = "avares://SVL.Avalonia/Assets/Icons/icon.png",
                    ["settings.card.basic"] = "avares://SVL.Avalonia/Assets/Icons/Vanilla.png",
                    ["settings.card.download"] = "avares://SVL.Avalonia/Assets/Icons/Junimo.png",
                    ["settings.card.nexus"] = "avares://SVL.Avalonia/Assets/Icons/Modded.png",
                    ["settings.card.update"] = "avares://SVL.Avalonia/Assets/Icons/Junimo2.png",
                    ["settings.card.nxm"] = "avares://SVL.Avalonia/Assets/Icons/icon.png",
                    ["settings.card.personalization"] = "avares://SVL.Avalonia/Assets/Icons/Junimo2.png",
                    ["settings.card.other"] = "avares://SVL.Avalonia/Assets/Icons/Junimo.png",
                    ["settings.card.about"] = "avares://SVL.Avalonia/Assets/Icons/icon.png",
                    ["instance.source.steam"] = "avares://SVL.Avalonia/Assets/Icons/Vanilla.png",
                    ["instance.source.gog"] = "avares://SVL.Avalonia/Assets/Icons/Vanilla.png",
                    ["instance.source.manual"] = "avares://SVL.Avalonia/Assets/Icons/Modded.png",
                    ["instance.source.unknown"] = "avares://SVL.Avalonia/Assets/Icons/Junimo.png"
                }
            }
        };

    private readonly LocalizationService _localizationService;

    public event Action? ResourcesChanged;

    public string CurrentTheme { get; private set; } = DefaultTheme;

    public ReadOnlyCollection<string> SupportedThemes { get; } = new([DefaultTheme]);

    public ImageResourceService(LocalizationService localizationService)
    {
        _localizationService = localizationService;
        _localizationService.LanguageChanged += () => ResourcesChanged?.Invoke();
    }

    public string Get(string businessKey, string? language = null, string? theme = null)
    {
        if (string.IsNullOrWhiteSpace(businessKey))
        {
            return string.Empty;
        }

        var targetTheme = string.IsNullOrWhiteSpace(theme) ? CurrentTheme : theme;
        var targetLanguage = string.IsNullOrWhiteSpace(language) ? _localizationService.CurrentLanguage : language;

        if (TryResolve(targetTheme, targetLanguage, businessKey, out var value))
        {
            return value;
        }

        if (TryResolve(targetTheme, NeutralLanguage, businessKey, out value))
        {
            return value;
        }

        if (TryResolve(DefaultTheme, targetLanguage, businessKey, out value))
        {
            return value;
        }

        if (TryResolve(DefaultTheme, NeutralLanguage, businessKey, out value))
        {
            return value;
        }

        return string.Empty;
    }

    public void SetTheme(string theme)
    {
        if (string.IsNullOrWhiteSpace(theme) ||
            !Catalog.ContainsKey(theme) ||
            string.Equals(CurrentTheme, theme, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        CurrentTheme = theme;
        ResourcesChanged?.Invoke();
    }

    private static bool TryResolve(string theme, string language, string businessKey, out string value)
    {
        value = string.Empty;

        if (!Catalog.TryGetValue(theme, out var languageCatalog))
        {
            return false;
        }

        if (!languageCatalog.TryGetValue(language, out var keyCatalog))
        {
            return false;
        }

        if (!keyCatalog.TryGetValue(businessKey, out var resolved))
        {
            return false;
        }

        value = resolved ?? string.Empty;
        return true;
    }
}
