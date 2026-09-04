using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using SVL.Avalonia.Models;
using System;
using System.Collections.Generic;

namespace SVL.Avalonia.Services;

public enum ThemeStyleType
{
    Stardew,
    MaterialYou
}

public enum MaterialScheme
{
    Blue,
    Green,
    Purple,
    Teal,
    Pink,
    Orange
}

public sealed class ThemeInfo
{
    public string DisplayName { get; init; } = "";
    public ThemeStyleType Style { get; init; }
    public MaterialScheme? Scheme { get; init; }
    public Color PreviewColor { get; init; }
    public Dictionary<string, Color> Colors { get; init; } = new();
}

public static class ThemeService
{
    private static ThemeStyleType _currentStyle = ThemeStyleType.Stardew;
    private static MaterialScheme _currentScheme = MaterialScheme.Blue;
    private static bool _isDarkMode;

    public static ThemeStyleType CurrentStyle => _currentStyle;
    public static MaterialScheme CurrentScheme => _currentScheme;
    public static bool IsDarkMode => _isDarkMode;

    /// <summary>
    /// 主题或暗色模式变更时触发。独立窗口（如 DebugConsoleWindow）可订阅此事件
    /// 主动将 Application.Resources 中的当前值复制到自身 Resources，
    /// 规避 Avalonia 对非主窗口 DynamicResource 传播不稳定的问题。
    /// </summary>
    public static event Action? ThemeChanged;

    public static IReadOnlyList<ThemeInfo> GetAvailableThemes()
    {
        return
        [
            new ThemeInfo { DisplayName = "星露谷（默认）", Style = ThemeStyleType.Stardew, PreviewColor = Color.Parse("#D47748"), Colors = GetStardewColors() },
            MakeMaterialTheme(MaterialScheme.Blue, "天空蓝", "#1976D2"),
            MakeMaterialTheme(MaterialScheme.Green, "森林绿", "#388E3C"),
            MakeMaterialTheme(MaterialScheme.Purple, "薰衣紫", "#7B1FA2"),
            MakeMaterialTheme(MaterialScheme.Teal, "海洋青", "#00796B"),
            MakeMaterialTheme(MaterialScheme.Pink, "樱花粉", "#C2185B"),
            MakeMaterialTheme(MaterialScheme.Orange, "夕阳橙", "#E64A19"),
        ];
    }

    public static void ApplyTheme(ThemeInfo theme)
    {
        var resources = Application.Current?.Resources;
        if (resources == null)
        {
            System.Diagnostics.Debug.WriteLine("[ThemeService] ERROR: Application.Current.Resources is NULL");
            return;
        }

        ApplyThemeResources(theme.Colors);
        System.Diagnostics.Debug.WriteLine($"[ThemeService] Applied theme '{theme.DisplayName}' ({theme.Colors.Count} resources)");

        _currentStyle = theme.Style;
        if (theme.Scheme.HasValue) _currentScheme = theme.Scheme.Value;

        // 深色模式覆盖
        if (_isDarkMode)
        {
            ApplyDarkOverrides();
            // 暗色覆盖后重新应用主题强调色，避免所有主题在暗色模式下都变成固定紫色
            ReapplyThemeAccent(theme);
        }

        ThemeChanged?.Invoke();
    }

    public static void SetDarkMode(bool dark)
    {
        _isDarkMode = dark;

        // 不使用 RequestedThemeVariant —— 它会让 FluentTheme 注入暗色资源覆盖自定义色
        // 保持 Light 变体，手动管理所有颜色
        if (Application.Current != null)
        {
            Application.Current.RequestedThemeVariant = ThemeVariant.Light;
        }

        if (dark)
        {
            ApplyDarkOverrides();
            // 暗色覆盖后重新应用当前主题强调色
            var themes = GetAvailableThemes();
            foreach (var t in themes)
            {
                if (t.Style == _currentStyle && t.Scheme == _currentScheme)
                {
                    ReapplyThemeAccent(t);
                    break;
                }
            }
        }
        else
        {
            // 亮色模式：
            // 1. 清除 FluentTheme 内置键的暗色覆盖（让 Light 变体默认值重新生效）
            // 2. 应用自定义键的浅色默认值（覆盖可能残留的暗色值）
            // 3. 应用当前主题色（覆盖为主题特定值）
            ClearFluentDarkOverrides();
            ApplyDefaultLightColors();
            var themes = GetAvailableThemes();
            foreach (var t in themes)
            {
                if (t.Style == _currentStyle && t.Scheme == _currentScheme)
                {
                    ApplyThemeResources(t.Colors);
                    break;
                }
            }
        }

        System.Diagnostics.Debug.WriteLine($"[ThemeService] Dark mode: {dark}");

        ThemeChanged?.Invoke();
    }

    private static void ApplyThemeResources(Dictionary<string, Color> colors)
    {
        var resources = Application.Current?.Resources;
        if (resources == null) return;
        foreach (var (key, color) in colors)
        {
            resources[key] = new SolidColorBrush(color);
        }
    }

    /// <summary>
    /// 暗色模式下重新应用主题强调色。
    /// 暗色背景需要更亮的强调色，将主题 AccentBrush 向白色混合 25% 提亮，
    /// 同时同步更新 PillBg/CategorySelectedBg/CategoryIndicator 等派生色。
    /// </summary>
    private static void ReapplyThemeAccent(ThemeInfo theme)
    {
        var resources = Application.Current?.Resources;
        if (resources == null) return;

        if (!theme.Colors.TryGetValue("AccentBrush", out var accentColor))
            return;

        // 暗色模式下将强调色向白色混合 25%，提高在深色背景上的可见度
        var lightened = Color.FromArgb(0xFF,
            (byte)(accentColor.R + (255 - accentColor.R) * 0.25),
            (byte)(accentColor.G + (255 - accentColor.G) * 0.25),
            (byte)(accentColor.B + (255 - accentColor.B) * 0.25));

        resources["AccentBrush"] = new SolidColorBrush(lightened);

        // 同步更新派生色（半透明强调色背景）
        var rgbHex = $"{lightened.R:X2}{lightened.G:X2}{lightened.B:X2}";
        resources["PillBg"] = new SolidColorBrush(Color.Parse($"#33{rgbHex}"));
        resources["CategorySelectedBg"] = new SolidColorBrush(Color.Parse($"#33{rgbHex}"));
        resources["CategoryIndicator"] = new SolidColorBrush(lightened);
        resources["ColorBrush2"] = new SolidColorBrush(lightened);
    }

    // 暗色覆盖资源键（静态字段，供 ApplyDarkOverrides 和 ClearDarkOverrides 共用）
    private static readonly Dictionary<string, string> DarkColorOverrides = new()
    {
            ["WindowBackgroundBrush"] = "#1E1E1E",
            ["PanelBackgroundBrush"] = "#2D2D2D",
            ["PanelBorderBrush"] = "#3D3D3D",
            ["HeaderBackgroundBrush"] = "#3C3C3C",
            ["AccentBrush"] = "#BB86FC",
            ["SurfaceBrush"] = "#252526",
            ["CardBrush"] = "#333333",
            ["BorderBrush"] = "#3D3D3D",
            ["TextPrimaryBrush"] = "#E0E0E0",
            ["TextSecondaryBrush"] = "#B0B0B0",
            ["NavTextBrush"] = "#FFFFFFFF",
            ["DefaultTextForeground"] = "#E0E0E0",
            ["PillBg"] = "#33BB86FC",
            ["CategorySelectedBg"] = "#33BB86FC",
            ["CategoryIndicator"] = "#BB86FC",
            ["WarningBg"] = "#33FFA000",
            ["WarningBorder"] = "#FFA000",
            ["IconBg"] = "#3C3C3C",
            ["IconBorder"] = "#555555",
            ["CardHighlightBg"] = "#1A90CAF9",
            ["CardHighlightBorder"] = "#3390CAF9",
            ["ColorBrushGray1"] = "#B0B0B0",
            ["ColorBrushGray2"] = "#A0A0A0",
            ["ColorBrushGray3"] = "#909090",
            ["ColorBrushGray4"] = "#707070",
            ["ColorBrushGray5"] = "#404040",
            ["ColorBrushGray6"] = "#333333",
            ["ColorBrushGray7"] = "#2D2D2D",
            ["ColorBrushGray8"] = "#252526",
            // ColorBrush 系列：暗黑模式下调整以确保可读性
            ["ColorBrush1"] = "#E0E0E0",
            ["ColorBrush2"] = "#BB86FC",
            ["ColorBrush3"] = "#F0C060",
            ["ColorBrush4"] = "#1A3A5C",
            ["ColorBrush5"] = "#4A4A3A",
            ["ColorBrush6"] = "#5E8FCC",
            ["ColorBrush7"] = "#6BA8E8",
            ["ColorBrush8"] = "#7DD0F0",
            ["ColorBrushSuccess"] = "#7DD0F0",
            ["ColorBrushSuccessLight"] = "#5EC3E6",
            ["ColorBrushRedBack"] = "#4A2A1A",
            ["ColorBrushRedLight"] = "#8B5A3A",
            ["ColorBrushRedDark"] = "#E09060",
            ["ColorBrushFatal"] = "#E06060",
            ["ColorBrushError"] = "#E09060",
            ["ColorBrushWarn"] = "#F0C060",
            ["ColorBrushInfoDark"] = "#E0E0E0",
            ["ColorBrushInfo"] = "#4A4A3A",
            ["ColorBrushDebug"] = "#909090",
        };

    // FluentTheme 内置控件暗色覆盖（TextBox/ComboBox/ListBox/Button 等）
    private static readonly Dictionary<string, string> FluentDarkColorOverrides = new()
    {
            ["TextForegroundFillColorPrimaryBrush"] = "#E0E0E0",
            ["TextForegroundFillColorSecondaryBrush"] = "#B0B0B0",
            ["TextForegroundFillColorTertiaryBrush"] = "#808080",
            ["TextForegroundFillColorDisabledBrush"] = "#606060",
            ["ControlFillColorDefaultBrush"] = "#2D2D2D",
            ["ControlFillColorSecondaryBrush"] = "#333333",
            ["ControlFillColorTertiaryBrush"] = "#3C3C3C",
            ["ControlFillColorDisabledBrush"] = "#252526",
            ["ControlFillColorInputActiveBrush"] = "#252526",
            ["ControlStrokeColorDefaultBrush"] = "#3D3D3D",
            ["ControlStrokeColorSecondaryBrush"] = "#4D4D4D",
            ["ControlStrokeColorOnAccentDefaultBrush"] = "#1E1E1E",
            ["ControlStrokeColorOnAccentSecondaryBrush"] = "#333333",
            ["ControlStrokeColorOnAccentTertiaryBrush"] = "#4D4D4D",
            ["SubtleFillColorTransparentBrush"] = "#00FFFFFF",
            ["SubtleFillColorSecondaryBrush"] = "#1AFFFFFF",
            ["SubtleFillColorTertiaryBrush"] = "#33FFFFFF",
            ["SubtleFillColorDisabledBrush"] = "#00FFFFFF",
            ["CardBackgroundFillColorDefaultBrush"] = "#333333",
            ["CardBackgroundFillColorSecondaryBrush"] = "#2D2D2D",
            ["LayerFillColorDefaultBrush"] = "#333333",
            ["LayerFillColorAltBrush"] = "#2D2D2D",
            ["SolidBackgroundFillColorBaseBrush"] = "#1E1E1E",
            ["SolidBackgroundFillColorSecondaryBrush"] = "#252526",
            ["SolidBackgroundFillColorTertiaryBrush"] = "#2D2D2D",
            ["SolidBackgroundFillColorQuarternaryBrush"] = "#333333",
            ["SurfaceFillColorDefaultBrush"] = "#2D2D2D",
            ["SurfaceFillColorSecondaryBrush"] = "#252526",
            ["SurfaceFillColorTertiaryBrush"] = "#1E1E1E",
            ["SystemControlBackgroundAltHighBrush"] = "#1E1E1E",
            ["SystemControlBackgroundBaseHighBrush"] = "#E0E0E0",
            ["SystemControlForegroundBaseHighBrush"] = "#E0E0E0",
            ["SystemControlForegroundBaseLowBrush"] = "#B0B0B0",
            // CheckBox/RadioButton 交互态前景
            ["CheckBoxForegroundUnchecked"] = "#E0E0E0",
            ["CheckBoxForegroundChecked"] = "#E0E0E0",
            ["CheckBoxForegroundUncheckedPressed"] = "#E0E0E0",
            ["CheckBoxForegroundCheckedPressed"] = "#E0E0E0",
            ["CheckBoxForegroundUncheckedDisabled"] = "#606060",
            ["CheckBoxForegroundCheckedDisabled"] = "#606060",
            ["CheckBoxForegroundPointerOver"] = "#E0E0E0",
            ["RadioButtonForegroundOuterEllipseChecked"] = "#E0E0E0",
            ["RadioButtonForegroundOuterEllipseUnchecked"] = "#E0E0E0",
            // ToggleSwitch
            ["ToggleSwitchContentForeground"] = "#E0E0E0",
            ["ToggleSwitchHeaderForeground"] = "#E0E0E0",
            // Expander/TabItem
            ["ExpanderHeaderForeground"] = "#E0E0E0",
            ["TabItemHeaderForegroundSelected"] = "#E0E0E0",
            ["TabItemHeaderForegroundUnselected"] = "#B0B0B0",
            ["TabItemHeaderForegroundPointerOver"] = "#E0E0E0",
            // ListBoxItem
            ["ListBoxItemForeground"] = "#E0E0E0",
            // Hyperlink
            ["HyperlinkButtonForeground"] = "#BB86FC",
            // Button 交互态
            ["ButtonBackgroundPressed"] = "#44555555",
            ["ButtonBackgroundPointerOver"] = "#33555555",
            ["ButtonForeground"] = "#E0E0E0",
            ["ButtonForegroundPointerOver"] = "#FFFFFF",
            ["ButtonForegroundPressed"] = "#FFFFFF",
            // Tooltip / Flyout backgrounds - must be DARK so light text is visible
            // SystemControlFlyoutBackgroundTransientBrush 是 FluentTheme Flyout/Tooltip 实际使用的背景资源
            ["SystemControlFlyoutBackgroundTransientBrush"] = "#2D2D2D",
            ["SystemControlFlyoutBorderBrushTransient"] = "#555555",
            ["BackgroundFillColorFlyoutBrush"] = "#2D2D2D",
            ["FlyoutBorderBrush"] = "#555555",
            ["TextFillColorInverseBrush"] = "#E0E0E0",
            // FluentTheme 实际资源键为大写 T 的 ToolTip*
            ["ToolTipBackground"] = "#2D2D2D",
            ["ToolTipBorderBrush"] = "#555555",
            ["ToolTipForeground"] = "#E0E0E0",
            // 兼容小写键
            ["TooltipBackground"] = "#2D2D2D",
            ["TooltipBorderBrush"] = "#555555",
            ["TooltipForeground"] = "#E0E0E0",
            // 搜索框/TextBox 聚焦态
            ["TextControlBackgroundFocused"] = "#252526",
            ["TextControlBorderBrushFocused"] = "#BB86FC",
            ["TextControlForeground"] = "#E0E0E0",
            ["TextControlForegroundFocused"] = "#E0E0E0",
            ["TextControlPlaceholderForeground"] = "#808080",
            ["TextControlPlaceholderForegroundFocused"] = "#909090",
            // ComboBox 下拉
            ["ComboBoxBackground"] = "#2D2D2D",
            ["ComboBoxBackgroundUnfocused"] = "#2D2D2D",
            ["ComboBoxBackgroundFocused"] = "#252526",
            ["ComboBoxBackgroundPressed"] = "#333333",
            ["ComboBoxForeground"] = "#E0E0E0",
            ["ComboBoxForegroundUnfocused"] = "#E0E0E0",
            ["ComboBoxBorderBrush"] = "#555555",
            ["ComboBoxBorderBrushFocused"] = "#BB86FC",
            ["ComboBoxDropDownBackgroundPointerOver"] = "#333333",
            ["ComboBoxDropDownBackgroundPressed"] = "#3C3C3C",
            // ComboBox 弹出/下拉面板背景与边框 - must be DARK so light text is visible
            // FluentTheme ComboBox 模板实际使用的资源键为 ComboBoxDropDownBackground / ComboBoxDropDownBorderBrush
            ["ComboBoxDropDownBackground"] = "#2D2D2D",
            ["ComboBoxDropDownBorderBrush"] = "#555555",
            ["FlyoutPresenterBackground"] = "#2D2D2D",
            ["FlyoutPresenterBorderBrush"] = "#555555",
            // ComboBoxItem 各态
            ["ComboBoxItemBackground"] = "Transparent",
            ["ComboBoxItemBackgroundPointerOver"] = "#33555555",
            ["ComboBoxItemBackgroundPressed"] = "#44555555",
            ["ComboBoxItemBackgroundSelected"] = "#33858585",
            ["ComboBoxItemForeground"] = "#E0E0E0",
            ["ComboBoxItemForegroundPointerOver"] = "#FFFFFF",
            ["ComboBoxItemForegroundPressed"] = "#FFFFFF",
            ["ComboBoxItemForegroundSelected"] = "#FFFFFF",
        };

    private static void ApplyDarkOverrides()
    {
        var resources = Application.Current?.Resources;
        if (resources == null) return;

        foreach (var (key, hex) in DarkColorOverrides)
        {
            resources[key] = new SolidColorBrush(Color.Parse(hex));
        }

        foreach (var (key, hex) in FluentDarkColorOverrides)
        {
            resources[key] = new SolidColorBrush(Color.Parse(hex));
        }

        // 诊断日志：验证关键资源的实际值
        DumpResourceValue(resources, "TextPrimaryBrush");
        DumpResourceValue(resources, "TextForegroundFillColorPrimaryBrush");
        DumpResourceValue(resources, "DefaultTextForeground");
        DumpResourceValue(resources, "NavTextBrush");
        DumpResourceValue(resources, "ControlFillColorDefaultBrush");
        DumpResourceValue(resources, "SolidBackgroundFillColorBaseBrush");
    }

    /// <summary>
    /// 浅色模式默认颜色（与 App.axaml 初始值一致）。
    /// 用于在切换到浅色模式时覆盖可能残留的暗色值。
    /// 包含 DarkColorOverrides 中所有键的浅色对应值。
    /// </summary>
    private static readonly Dictionary<string, string> DefaultLightColors = new()
    {
        ["WindowBackgroundBrush"] = "#F5EDE0",
        ["PanelBackgroundBrush"] = "#FAF7EE",
        ["PanelBorderBrush"] = "#B89B7F",
        ["HeaderBackgroundBrush"] = "#D47748",
        ["AccentBrush"] = "#D47748",
        ["SurfaceBrush"] = "#FAF7EE",
        ["CardBrush"] = "#F8F2DE",
        ["BorderBrush"] = "#D4C4A8",
        ["TextPrimaryBrush"] = "#3A2B22",
        ["TextSecondaryBrush"] = "#8B7355",
        ["NavTextBrush"] = "#FFFFFFFF",
        ["DefaultTextForeground"] = "#3A2B22",
        ["PillBg"] = "#33D47748",
        ["CategorySelectedBg"] = "#33D4C4A8",
        ["CategoryIndicator"] = "#D47748",
        ["WarningBg"] = "#33F59E0B",
        ["WarningBorder"] = "#EAB308",
        ["IconBg"] = "#F7D38A",
        ["IconBorder"] = "#D4C4A8",
        ["CardHighlightBg"] = "#0CF7D38A",
        ["CardHighlightBorder"] = "#44D2A679",
        ["ColorBrushGray1"] = "#5C4033",
        ["ColorBrushGray2"] = "#8B7355",
        ["ColorBrushGray3"] = "#A0826D",
        ["ColorBrushGray4"] = "#B89B7F",
        ["ColorBrushGray5"] = "#D4C4A8",
        ["ColorBrushGray6"] = "#EBE0CC",
        ["ColorBrushGray7"] = "#F5EDE0",
        ["ColorBrushGray8"] = "#FAF7EE",
        ["ColorBrush1"] = "#663021",
        ["ColorBrush2"] = "#D47748",
        ["ColorBrush3"] = "#FED183",
        ["ColorBrush4"] = "#D8E8F6",
        ["ColorBrush5"] = "#E7E0B1",
        ["ColorBrush6"] = "#0455A4",
        ["ColorBrush7"] = "#158BF1",
        ["ColorBrush8"] = "#5EC3E6",
        ["ColorBrushSuccess"] = "#5EC3E6",
        ["ColorBrushSuccessLight"] = "#94D9F0",
        ["ColorBrushRedBack"] = "#FFE0CC",
        ["ColorBrushRedLight"] = "#FFB38A",
        ["ColorBrushRedDark"] = "#D47748",
        ["ColorBrushFatal"] = "#8B4033",
        ["ColorBrushError"] = "#D47748",
        ["ColorBrushWarn"] = "#FED183",
        ["ColorBrushInfoDark"] = "#663021",
        ["ColorBrushInfo"] = "#E7E0B1",
        ["ColorBrushDebug"] = "#A0826D",
    };

    /// <summary>
    /// 清除 FluentTheme 内置键的暗色覆盖，让 Light 变体默认值重新生效。
    /// 只移除 FluentDarkColorOverrides 中的键，不动自定义 PCLTheme 键
    /// （自定义键由 ApplyDefaultLightColors 和 ApplyThemeResources 负责设置）。
    /// </summary>
    private static void ClearFluentDarkOverrides()
    {
        var resources = Application.Current?.Resources;
        if (resources == null) return;

        foreach (var key in FluentDarkColorOverrides.Keys)
        {
            resources.Remove(key);
        }
    }

    /// <summary>
    /// 应用自定义键的浅色默认值，覆盖可能残留的暗色值。
    /// </summary>
    private static void ApplyDefaultLightColors()
    {
        var resources = Application.Current?.Resources;
        if (resources == null) return;

        foreach (var (key, hex) in DefaultLightColors)
        {
            resources[key] = new SolidColorBrush(Color.Parse(hex));
        }
    }

    private static void DumpResourceValue(object resources, string key)
    {
        try
        {
            if (resources is ResourceDictionary dict && dict.ContainsKey(key))
            {
                var val = dict[key];
                if (val is SolidColorBrush brush)
                    System.Diagnostics.Debug.WriteLine($"[ThemeService]   {key} = #{brush.Color.A:X2}{brush.Color.R:X2}{brush.Color.G:X2}{brush.Color.B:X2}");
                else
                    System.Diagnostics.Debug.WriteLine($"[ThemeService]   {key} = {val?.GetType().Name}");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[ThemeService]   {key} = NOT FOUND");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ThemeService]   {key} = ERROR: {ex.Message}");
        }
    }

    public static void RestoreFromSettings(AppUserSettings settings)
    {
        _isDarkMode = settings.ThemeMode.Contains("深") || settings.ThemeMode.Contains("暗") || settings.ThemeMode.Contains("Dark", StringComparison.OrdinalIgnoreCase);

        if (Enum.TryParse<ThemeStyleType>(settings.ThemeStyleName, out var style))
        {
            _currentStyle = style;
            if (style == ThemeStyleType.MaterialYou &&
                Enum.TryParse<MaterialScheme>(settings.ThemeColorScheme, out var scheme))
            {
                _currentScheme = scheme;
            }
        }

        var themes = GetAvailableThemes();
        ThemeInfo? target = null;
        foreach (var t in themes)
        {
            if (t.Style == _currentStyle && t.Scheme == _currentScheme)
            {
                target = t;
                break;
            }
        }

        if (target != null) ApplyTheme(target);

        // 设置主题变体（必须在 ApplyTheme 之后）
        SetDarkMode(_isDarkMode);
    }

    public static void SaveToSettings(AppUserSettings settings)
    {
        settings.ThemeStyleName = _currentStyle.ToString();
        settings.ThemeColorScheme = _currentScheme.ToString();
    }

    private static Dictionary<string, Color> GetStardewColors()
    {
        return new Dictionary<string, Color>
        {
            ["WindowBackgroundBrush"] = Color.Parse("#F5EDE0"),
            ["HeaderBackgroundBrush"] = Color.Parse("#D47748"),
            ["PanelBackgroundBrush"] = Color.Parse("#FAF7EE"),
            ["PanelBorderBrush"] = Color.Parse("#B89B7F"),
            ["AccentBrush"] = Color.Parse("#D47748"),
            ["SurfaceBrush"] = Color.Parse("#FAF7EE"),
            ["CardBrush"] = Color.Parse("#F8F2DE"),
            ["BorderBrush"] = Color.Parse("#D4C4A8"),
            ["TextPrimaryBrush"] = Color.Parse("#3A2B22"),
            ["TextSecondaryBrush"] = Color.Parse("#8B7355"),
            ["ColorBrush1"] = Color.Parse("#663021"),
            ["ColorBrush2"] = Color.Parse("#D47748"),
            ["ColorBrush3"] = Color.Parse("#FED183"),
            ["ColorBrush4"] = Color.Parse("#D8E8F6"),
            ["ColorBrush5"] = Color.Parse("#E7E0B1"),
            ["ColorBrush6"] = Color.Parse("#0455A4"),
            ["ColorBrush7"] = Color.Parse("#158BF1"),
            ["ColorBrush8"] = Color.Parse("#5EC3E6"),
            ["CategorySelectedBg"] = Color.Parse("#33D4C4A8"),
            ["CategoryIndicator"] = Color.Parse("#D47748"),
            ["WarningBg"] = Color.Parse("#33F59E0B"),
            ["WarningBorder"] = Color.Parse("#EAB308"),
            ["IconBg"] = Color.Parse("#F7D38A"),
            ["IconBorder"] = Color.Parse("#D4C4A8"),
            ["CardHighlightBg"] = Color.Parse("#0CF7D38A"),
            ["CardHighlightBorder"] = Color.Parse("#44D2A679"),
            ["PillBg"] = Color.Parse("#33D47748"),
        };
    }

    private static ThemeInfo MakeMaterialTheme(MaterialScheme scheme, string name, string primary)
    {
        var primaryColor = Color.Parse(primary);
        // 浅色变体：将主色与白色混合 75%，用于原版Tag等次要标识
        var lightVariant = Color.FromArgb(0xFF,
            (byte)(primaryColor.R + (255 - primaryColor.R) * 0.75),
            (byte)(primaryColor.G + (255 - primaryColor.G) * 0.75),
            (byte)(primaryColor.B + (255 - primaryColor.B) * 0.75));
        var colors = new Dictionary<string, Color>
        {
            ["WindowBackgroundBrush"] = Color.Parse("#FAFAFA"),
            ["HeaderBackgroundBrush"] = primaryColor,
            ["PanelBackgroundBrush"] = Color.Parse("#FFFFFF"),
            ["PanelBorderBrush"] = Color.Parse("#E0E0E0"),
            ["AccentBrush"] = primaryColor,
            ["SurfaceBrush"] = Color.Parse("#FAFAFA"),
            ["CardBrush"] = Color.Parse("#FFFFFF"),
            ["BorderBrush"] = Color.Parse("#E0E0E0"),
            ["TextPrimaryBrush"] = Color.Parse("#1C1B1F"),
            ["TextSecondaryBrush"] = Color.Parse("#49454F"),
            ["ColorBrush1"] = Color.Parse("#1C1B1F"),
            ["ColorBrush2"] = primaryColor,
            ["ColorBrush3"] = lightVariant,
            ["ColorBrush5"] = Color.Parse("#FAFAFA"),
            ["CategorySelectedBg"] = Color.Parse("#33C4C0C9"),
            ["CategoryIndicator"] = primaryColor,
            ["WarningBg"] = Color.Parse("#33FFA000"),
            ["WarningBorder"] = Color.Parse("#FFA000"),
            ["IconBg"] = Color.Parse("#E8E8E8"),
            ["IconBorder"] = Color.Parse("#C4C0C9"),
            ["CardHighlightBg"] = Color.Parse("#0CE3F2FD"),
            ["CardHighlightBorder"] = Color.Parse("#4490CAF9"),
            ["PillBg"] = Color.FromArgb(0x33, primaryColor.R, primaryColor.G, primaryColor.B),
        };

        return new ThemeInfo
        {
            DisplayName = name, // 使用短名称（"天空蓝"），与 ThemeStyleOptions 一致
            Style = ThemeStyleType.MaterialYou,
            Scheme = scheme,
            PreviewColor = primaryColor,
            Colors = colors
        };
    }
}
