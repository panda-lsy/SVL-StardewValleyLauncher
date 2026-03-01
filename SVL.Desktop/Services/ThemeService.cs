using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SVL.Core.Config;
using SVL.Core.Logging;

namespace SVL.Desktop.Services;

/// <summary>
/// 主题风格类型
/// </summary>
public enum ThemeStyle
{
    /// <summary>默认星露谷风格（暖棕橙调）</summary>
    Stardew,
    /// <summary>Material You 风格</summary>
    MaterialYou
}

/// <summary>
/// Material You 配色方案
/// </summary>
public enum MaterialYouColorScheme
{
    Blue,
    Green,
    Purple,
    Teal,
    Pink,
    Orange
}

/// <summary>
/// 主题定义：包含所有 DynamicResource 的颜色值
/// </summary>
public class ThemeDefinition
{
    public string DisplayName { get; set; } = "";
    public ThemeStyle Style { get; set; }
    public MaterialYouColorScheme? ColorScheme { get; set; }

    // 主色调 1-8
    public Color Color1 { get; set; }
    public Color Color2 { get; set; }
    public Color Color3 { get; set; }
    public Color Color4 { get; set; }
    public Color Color5 { get; set; }
    public Color Color6 { get; set; }
    public Color Color7 { get; set; }
    public Color Color8 { get; set; }

    // 背景色
    public Color ColorBg0 { get; set; }
    public Color ColorBg1 { get; set; }
    public Color ColorBackground { get; set; }
    public Color ColorTransparentBackground { get; set; }

    // 灰度色系 1-8
    public Color Gray1 { get; set; }
    public Color Gray2 { get; set; }
    public Color Gray3 { get; set; }
    public Color Gray4 { get; set; }
    public Color Gray5 { get; set; }
    public Color Gray6 { get; set; }
    public Color Gray7 { get; set; }
    public Color Gray8 { get; set; }

    // 状态色
    public Color ColorSuccess { get; set; }
    public Color ColorSuccessLight { get; set; }
    public Color ColorRedBack { get; set; }
    public Color ColorRedLight { get; set; }
    public Color ColorRedDark { get; set; }

    // 透明度色
    public Color ColorHalfWhite { get; set; }
    public Color ColorSemiWhite { get; set; }
    public Color ColorSemiTransparent { get; set; }
    public Color ColorToolTip { get; set; }

    // 日志色
    public Color ColorFatal { get; set; }
    public Color ColorError { get; set; }
    public Color ColorWarn { get; set; }
    public Color ColorInfoDark { get; set; }
    public Color ColorInfo { get; set; }
    public Color ColorDebug { get; set; }
}

/// <summary>
/// 主题服务：管理主题切换，运行时替换 DynamicResource 色值
/// </summary>
public static class ThemeService
{
    private static ThemeStyle _currentStyle = ThemeStyle.Stardew;
    private static MaterialYouColorScheme _currentColorScheme = MaterialYouColorScheme.Blue;
    private static bool _animateTransitions = true;
    private static ThemeMode _currentThemeMode = ThemeMode.System;
    private static bool _transparencyEnabled = true;

    /// <summary>当前主题风格</summary>
    public static ThemeStyle CurrentStyle => _currentStyle;

    /// <summary>当前 Material You 配色方案</summary>
    public static MaterialYouColorScheme CurrentColorScheme => _currentColorScheme;

    /// <summary>是否启用主题切换动画</summary>
    public static bool AnimateTransitions
    {
        get => _animateTransitions;
        set => _animateTransitions = value;
    }

    /// <summary>当前明暗模式</summary>
    public static ThemeMode CurrentThemeMode => _currentThemeMode;

    /// <summary>是否启用透明效果</summary>
    public static bool TransparencyEnabled => _transparencyEnabled;

    /// <summary>主题变更事件</summary>
    public static event Action<ThemeStyle, MaterialYouColorScheme?>? ThemeChanged;

    /// <summary>
    /// 获取所有可用的主题定义
    /// </summary>
    public static IReadOnlyList<ThemeDefinition> GetAvailableThemes()
    {
        return new List<ThemeDefinition>
        {
            CreateStardewTheme(),
            CreateMaterialYouTheme(MaterialYouColorScheme.Blue),
            CreateMaterialYouTheme(MaterialYouColorScheme.Green),
            CreateMaterialYouTheme(MaterialYouColorScheme.Purple),
            CreateMaterialYouTheme(MaterialYouColorScheme.Teal),
            CreateMaterialYouTheme(MaterialYouColorScheme.Pink),
            CreateMaterialYouTheme(MaterialYouColorScheme.Orange),
        };
    }

    /// <summary>
    /// 获取 Material You 配色方案的显示名称
    /// </summary>
    public static string GetColorSchemeName(MaterialYouColorScheme scheme)
    {
        return scheme switch
        {
            MaterialYouColorScheme.Blue => "天空蓝",
            MaterialYouColorScheme.Green => "森林绿",
            MaterialYouColorScheme.Purple => "薰衣紫",
            MaterialYouColorScheme.Teal => "海洋青",
            MaterialYouColorScheme.Pink => "樱花粉",
            MaterialYouColorScheme.Orange => "夕阳橙",
            _ => scheme.ToString()
        };
    }

    /// <summary>
    /// 获取 Material You 配色方案的预览颜色（主强调色）
    /// </summary>
    public static Color GetColorSchemePreviewColor(MaterialYouColorScheme scheme)
    {
        return scheme switch
        {
            MaterialYouColorScheme.Blue => ParseColor("#1976D2"),
            MaterialYouColorScheme.Green => ParseColor("#388E3C"),
            MaterialYouColorScheme.Purple => ParseColor("#7B1FA2"),
            MaterialYouColorScheme.Teal => ParseColor("#00796B"),
            MaterialYouColorScheme.Pink => ParseColor("#C2185B"),
            MaterialYouColorScheme.Orange => ParseColor("#E64A19"),
            _ => ParseColor("#1976D2")
        };
    }

    /// <summary>
    /// 应用主题（Stardew 默认）
    /// </summary>
    public static void ApplyStardewTheme()
    {
        var theme = CreateStardewTheme();
        ApplyTheme(theme);
        _currentStyle = ThemeStyle.Stardew;
        ThemeChanged?.Invoke(ThemeStyle.Stardew, null);
        Log.Info("[ThemeService] 已切换到默认星露谷主题");
    }

    /// <summary>
    /// 应用 Material You 主题
    /// </summary>
    public static void ApplyMaterialYouTheme(MaterialYouColorScheme scheme)
    {
        var theme = CreateMaterialYouTheme(scheme);
        ApplyTheme(theme);
        _currentStyle = ThemeStyle.MaterialYou;
        _currentColorScheme = scheme;
        ThemeChanged?.Invoke(ThemeStyle.MaterialYou, scheme);
        Log.Info($"[ThemeService] 已切换到 Material You 主题: {GetColorSchemeName(scheme)}");
    }

    /// <summary>
    /// 从配置恢复主题
    /// </summary>
    public static void RestoreFromConfig()
    {
        try
        {
            var settings = AppConfig.GetSettings();
            _currentThemeMode = settings.ThemeMode;
            _transparencyEnabled = settings.EnableTransparency;
            var styleStr = settings.ThemeStyleName ?? "Stardew";
            var schemeStr = settings.ThemeColorScheme ?? "Blue";

            if (Enum.TryParse<ThemeStyle>(styleStr, out var style))
            {
                if (style == ThemeStyle.MaterialYou && Enum.TryParse<MaterialYouColorScheme>(schemeStr, out var scheme))
                {
                    ApplyMaterialYouTheme(scheme);
                }
                else
                {
                    ApplyStardewTheme();
                }
            }
            else
            {
                ApplyStardewTheme();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[ThemeService] 恢复主题配置失败: {ex.Message}");
            ApplyStardewTheme();
        }
    }

    /// <summary>
    /// 设置明暗模式并立即重应用当前主题
    /// </summary>
    public static void SetThemeMode(ThemeMode mode)
    {
        _currentThemeMode = mode;
        ReapplyCurrentTheme();
    }

    /// <summary>
    /// 设置透明效果开关并立即重应用当前主题
    /// </summary>
    public static void SetTransparencyEnabled(bool enabled)
    {
        _transparencyEnabled = enabled;
        ReapplyCurrentTheme();
    }

    /// <summary>
    /// 保存当前主题到配置
    /// </summary>
    public static void SaveToConfig()
    {
        try
        {
            var settings = AppConfig.GetSettings();
            settings.ThemeStyleName = _currentStyle.ToString();
            settings.ThemeColorScheme = _currentColorScheme.ToString();
            AppConfig.SaveSettings(settings);
        }
        catch (Exception ex)
        {
            Log.Warn($"[ThemeService] 保存主题配置失败: {ex.Message}");
        }
    }

    #region 主题定义

    /// <summary>
    /// 创建默认星露谷主题
    /// </summary>
    private static ThemeDefinition CreateStardewTheme()
    {
        return new ThemeDefinition
        {
            DisplayName = "星露谷（默认）",
            Style = ThemeStyle.Stardew,
            ColorScheme = null,

            // 主色调
            Color1 = ParseColor("#663021"),  // 深棕 - 主要文字
            Color2 = ParseColor("#D47748"),  // 橙棕 - 主要强调色
            Color3 = ParseColor("#FED183"),  // 金色 - 次要强调色
            Color4 = ParseColor("#D8E8F6"),  // 浅蓝白 - 背景浅色
            Color5 = ParseColor("#E7E0B1"),  // 米黄 - 卡片背景
            Color6 = ParseColor("#0455A4"),  // 深蓝 - 边框/分割线
            Color7 = ParseColor("#158BF1"),  // 亮蓝 - 辅助色
            Color8 = ParseColor("#5EC3E6"),  // 青蓝 - 高亮色

            // 背景色
            ColorBg0 = ParseColor("#0455A4"),
            ColorBg1 = ParseColor("#E7E0B1"),
            ColorBackground = ParseColor("#E7E0B1"),
            ColorTransparentBackground = ParseColor("#D2FAF7EE"),

            // 灰度色系
            Gray1 = ParseColor("#5C4033"),
            Gray2 = ParseColor("#8B7355"),
            Gray3 = ParseColor("#A0826D"),
            Gray4 = ParseColor("#B89B7F"),
            Gray5 = ParseColor("#D4C4A8"),
            Gray6 = ParseColor("#EBE0CC"),
            Gray7 = ParseColor("#F5EDE0"),
            Gray8 = ParseColor("#FAF7EE"),

            // 状态色
            ColorSuccess = ParseColor("#5EC3E6"),
            ColorSuccessLight = ParseColor("#94D9F0"),
            ColorRedBack = ParseColor("#80FFE0CC"),
            ColorRedLight = ParseColor("#FFB38A"),
            ColorRedDark = ParseColor("#D47748"),

            // 透明度色
            ColorHalfWhite = ParseColor("#55FFFFFF"),
            ColorSemiWhite = ParseColor("#BBFFFFFF"),
            ColorSemiTransparent = ParseColor("#01D8E8F6"),
            ColorToolTip = ParseColor("#E5FFFFFF"),

            // 日志色
            ColorFatal = ParseColor("#8B4033"),
            ColorError = ParseColor("#D47748"),
            ColorWarn = ParseColor("#FED183"),
            ColorInfoDark = ParseColor("#663021"),
            ColorInfo = ParseColor("#E7E0B1"),
            ColorDebug = ParseColor("#A0826D"),
        };
    }

    /// <summary>
    /// 创建 Material You 主题
    /// </summary>
    private static ThemeDefinition CreateMaterialYouTheme(MaterialYouColorScheme scheme)
    {
        // Material You 色系基于 Google Material Design 3 的色彩系统
        // 每个方案定义：Primary(主色), OnPrimary, PrimaryContainer, Surface 等
        var (primary, onPrimary, primaryContainer, secondary, surface, surfaceVariant,
             outline, surfaceContainerLow, surfaceContainerHigh, tertiaryColor) = GetMaterialColors(scheme);

        return new ThemeDefinition
        {
            DisplayName = $"Material You - {GetColorSchemeName(scheme)}",
            Style = ThemeStyle.MaterialYou,
            ColorScheme = scheme,

            // 主色调映射到 Material You 色彩角色
            Color1 = onPrimary,                          // 主要文字 → On Surface (深色)
            Color2 = primary,                             // 主要强调色 → Primary
            Color3 = primaryContainer,                    // 次要强调色 → Primary Container
            Color4 = surfaceVariant,                      // 背景浅色 → Surface Variant
            Color5 = surface,                             // 卡片背景 → Surface
            Color6 = secondary,                           // 边框/分割线 → Secondary
            Color7 = Lighten(primary, 0.2),               // 辅助色 → Primary lighter
            Color8 = tertiaryColor,                       // 高亮色 → Tertiary

            // 背景色
            ColorBg0 = primary,                           // 主背景 → Primary
            ColorBg1 = surface,                           // 次背景 → Surface
            ColorBackground = surfaceContainerLow,        // 页面背景 → Surface Container Low
            ColorTransparentBackground = WithAlpha(surfaceContainerHigh, 0.92), // 半透明卡片底

            // 灰度色系（Material 中性色调）
            Gray1 = ParseColor("#1C1B1F"),  // On Surface
            Gray2 = ParseColor("#49454F"),  // On Surface Variant
            Gray3 = ParseColor("#79747E"),  // Outline
            Gray4 = ParseColor("#938F99"),  // Outline Variant
            Gray5 = ParseColor("#C4C0C9"),  // Surface Variant border
            Gray6 = ParseColor("#E6E1E5"),  // Surface Variant
            Gray7 = ParseColor("#F4EFF4"),  // Surface Container High
            Gray8 = ParseColor("#FFFBFE"),  // Surface/Background

            // 状态色（Material Design 标准语义色）
            ColorSuccess = ParseColor("#4CAF50"),
            ColorSuccessLight = ParseColor("#81C784"),
            ColorRedBack = ParseColor("#80FFCDD2"),
            ColorRedLight = ParseColor("#EF9A9A"),
            ColorRedDark = ParseColor("#D32F2F"),

            // 透明度色
            ColorHalfWhite = ParseColor("#55FFFFFF"),
            ColorSemiWhite = ParseColor("#CCFFFFFF"),
            ColorSemiTransparent = WithAlpha(surfaceVariant, 0.04),
            ColorToolTip = ParseColor("#F0FFFFFF"),

            // 日志色
            ColorFatal = ParseColor("#B71C1C"),
            ColorError = ParseColor("#D32F2F"),
            ColorWarn = ParseColor("#FFA000"),
            ColorInfoDark = ParseColor("#1565C0"),
            ColorInfo = ParseColor("#BBDEFB"),
            ColorDebug = ParseColor("#78909C"),
        };
    }

    /// <summary>
    /// 获取 Material You 配色方案的核心颜色
    /// </summary>
    private static (Color primary, Color onPrimary, Color primaryContainer,
        Color secondary, Color surface, Color surfaceVariant,
        Color outline, Color surfaceContainerLow, Color surfaceContainerHigh,
        Color tertiary) GetMaterialColors(MaterialYouColorScheme scheme)
    {
        return scheme switch
        {
            MaterialYouColorScheme.Blue => (
                primary: ParseColor("#1976D2"),
                onPrimary: ParseColor("#1A237E"),
                primaryContainer: ParseColor("#BBDEFB"),
                secondary: ParseColor("#455A64"),
                surface: ParseColor("#FAFAFA"),
                surfaceVariant: ParseColor("#E3F2FD"),
                outline: ParseColor("#90A4AE"),
                surfaceContainerLow: ParseColor("#F5F5F5"),
                surfaceContainerHigh: ParseColor("#EEEEEE"),
                tertiary: ParseColor("#00BCD4")
            ),
            MaterialYouColorScheme.Green => (
                primary: ParseColor("#388E3C"),
                onPrimary: ParseColor("#1B5E20"),
                primaryContainer: ParseColor("#C8E6C9"),
                secondary: ParseColor("#5D4037"),
                surface: ParseColor("#FAFAFA"),
                surfaceVariant: ParseColor("#E8F5E9"),
                outline: ParseColor("#A1887F"),
                surfaceContainerLow: ParseColor("#F1F8E9"),
                surfaceContainerHigh: ParseColor("#E8F5E9"),
                tertiary: ParseColor("#FFC107")
            ),
            MaterialYouColorScheme.Purple => (
                primary: ParseColor("#7B1FA2"),
                onPrimary: ParseColor("#4A148C"),
                primaryContainer: ParseColor("#E1BEE7"),
                secondary: ParseColor("#455A64"),
                surface: ParseColor("#FAFAFA"),
                surfaceVariant: ParseColor("#F3E5F5"),
                outline: ParseColor("#90A4AE"),
                surfaceContainerLow: ParseColor("#F9F4FC"),
                surfaceContainerHigh: ParseColor("#F3E5F5"),
                tertiary: ParseColor("#FF7043")
            ),
            MaterialYouColorScheme.Teal => (
                primary: ParseColor("#00796B"),
                onPrimary: ParseColor("#004D40"),
                primaryContainer: ParseColor("#B2DFDB"),
                secondary: ParseColor("#37474F"),
                surface: ParseColor("#FAFAFA"),
                surfaceVariant: ParseColor("#E0F2F1"),
                outline: ParseColor("#78909C"),
                surfaceContainerLow: ParseColor("#F0FAF9"),
                surfaceContainerHigh: ParseColor("#E0F2F1"),
                tertiary: ParseColor("#FF8F00")
            ),
            MaterialYouColorScheme.Pink => (
                primary: ParseColor("#C2185B"),
                onPrimary: ParseColor("#880E4F"),
                primaryContainer: ParseColor("#F8BBD0"),
                secondary: ParseColor("#5D4037"),
                surface: ParseColor("#FAFAFA"),
                surfaceVariant: ParseColor("#FCE4EC"),
                outline: ParseColor("#A1887F"),
                surfaceContainerLow: ParseColor("#FFF8FA"),
                surfaceContainerHigh: ParseColor("#FCE4EC"),
                tertiary: ParseColor("#7C4DFF")
            ),
            MaterialYouColorScheme.Orange => (
                primary: ParseColor("#E64A19"),
                onPrimary: ParseColor("#BF360C"),
                primaryContainer: ParseColor("#FFCCBC"),
                secondary: ParseColor("#455A64"),
                surface: ParseColor("#FAFAFA"),
                surfaceVariant: ParseColor("#FBE9E7"),
                outline: ParseColor("#90A4AE"),
                surfaceContainerLow: ParseColor("#FFF8F5"),
                surfaceContainerHigh: ParseColor("#FBE9E7"),
                tertiary: ParseColor("#26A69A")
            ),
            _ => GetMaterialColors(MaterialYouColorScheme.Blue)
        };
    }

    #endregion

    #region 应用主题

    /// <summary>
    /// 应用主题定义到 Application.Resources（DynamicResource 会自动生效）
    /// </summary>
    private static void ApplyTheme(ThemeDefinition theme)
    {
        theme = AdjustThemeByMode(theme, ResolveEffectiveThemeMode(_currentThemeMode));

        var res = Application.Current.Resources;
        var animate = _animateTransitions;
        var duration = TimeSpan.FromMilliseconds(300);

        // 主色调
        SetBrushAndColor(res, "ColorBrush1", "ColorObject1", "ColorBrush1_Color", theme.Color1, animate, duration);
        SetBrushAndColor(res, "ColorBrush2", "ColorObject2", "ColorBrush2_Color", theme.Color2, animate, duration);
        SetBrushAndColor(res, "ColorBrush3", "ColorObject3", "ColorBrush3_Color", theme.Color3, animate, duration);
        SetBrushAndColor(res, "ColorBrush4", "ColorObject4", null, theme.Color4, animate, duration);
        SetBrushAndColor(res, "ColorBrush5", "ColorObject5", null, theme.Color5, animate, duration);
        SetBrushAndColor(res, "ColorBrush6", "ColorObject6", null, theme.Color6, animate, duration);
        SetBrushAndColor(res, "ColorBrush7", "ColorObject7", null, theme.Color7, animate, duration);
        SetBrushAndColor(res, "ColorBrush8", "ColorObject8", null, theme.Color8, animate, duration);

        // 背景色
        SetBrush(res, "ColorBrushBg0", theme.ColorBg0, animate, duration);
        SetBrush(res, "ColorBrushBg1", theme.ColorBg1, animate, duration);
        SetBrush(res, "ColorBrushBackground", theme.ColorBackground, animate, duration);
        var transparentBackground = _transparencyEnabled
            ? theme.ColorTransparentBackground
            : Color.FromArgb(0xFF, theme.ColorBackground.R, theme.ColorBackground.G, theme.ColorBackground.B);
        SetBrush(res, "ColorBrushTransparentBackground", transparentBackground, animate, duration);

        // 灰度色系
        SetBrushAndColor(res, "ColorBrushGray1", "ColorObjectGray1", null, theme.Gray1, animate, duration);
        SetBrushAndColor(res, "ColorBrushGray2", "ColorObjectGray2", null, theme.Gray2, animate, duration);
        SetBrushAndColor(res, "ColorBrushGray3", "ColorObjectGray3", null, theme.Gray3, animate, duration);
        SetBrushAndColor(res, "ColorBrushGray4", "ColorObjectGray4", null, theme.Gray4, animate, duration);
        SetBrushAndColor(res, "ColorBrushGray5", "ColorObjectGray5", null, theme.Gray5, animate, duration);
        SetBrushAndColor(res, "ColorBrushGray6", "ColorObjectGray6", null, theme.Gray6, animate, duration);
        SetBrushAndColor(res, "ColorBrushGray7", "ColorObjectGray7", null, theme.Gray7, animate, duration);
        SetBrushAndColor(res, "ColorBrushGray8", "ColorObjectGray8", null, theme.Gray8, animate, duration);

        // 状态色
        SetBrush(res, "ColorBrushSuccess", theme.ColorSuccess, animate, duration);
        SetBrush(res, "ColorBrushSuccessLight", theme.ColorSuccessLight, animate, duration);
        SetBrush(res, "ColorBrushRedBack", theme.ColorRedBack, animate, duration);
        SetBrush(res, "ColorBrushRedLight", theme.ColorRedLight, animate, duration);
        SetBrush(res, "ColorBrushRedDark", theme.ColorRedDark, animate, duration);

        // 透明度色
        SetBrush(res, "ColorBrushHalfWhite", theme.ColorHalfWhite, animate, duration);
        SetBrush(res, "ColorBrushSemiWhite", theme.ColorSemiWhite, animate, duration);
        SetBrush(res, "ColorBrushSemiTransparent", theme.ColorSemiTransparent, animate, duration);
        SetBrush(res, "ColorBrushToolTip", theme.ColorToolTip, animate, duration);

        // 日志色
        SetBrush(res, "ColorBrushFatal", theme.ColorFatal, animate, duration);
        SetBrush(res, "ColorBrushError", theme.ColorError, animate, duration);
        SetBrush(res, "ColorBrushWarn", theme.ColorWarn, animate, duration);
        SetBrush(res, "ColorBrushInfoDark", theme.ColorInfoDark, animate, duration);
        SetBrush(res, "ColorBrushInfo", theme.ColorInfo, animate, duration);
        SetBrush(res, "ColorBrushDebug", theme.ColorDebug, animate, duration);
        SetBrush(res, "ColorBrushMemory", theme.Color1, animate, duration);

        // 确保 ColorBrushMask 存在（修复缺失 Bug）
        if (!res.Contains("ColorBrushMask"))
        {
            res["ColorBrushMask"] = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0));
        }
    }

    /// <summary>
    /// 设置画刷 + 对应的 Color 对象
    /// </summary>
    private static void SetBrushAndColor(ResourceDictionary res, string brushKey,
        string? colorObjectKey, string? colorKey2, Color target, bool animate, TimeSpan duration)
    {
        SetBrush(res, brushKey, target, animate, duration);

        if (colorObjectKey != null)
            res[colorObjectKey] = target;

        if (colorKey2 != null)
            res[colorKey2] = target;
    }

    /// <summary>
    /// 设置 SolidColorBrush（可选动画过渡）
    /// </summary>
    private static void SetBrush(ResourceDictionary res, string key, Color target, bool animate, TimeSpan duration)
    {
        Color from = target;
        if (res.Contains(key) && res[key] is SolidColorBrush existing)
            from = existing.IsFrozen ? existing.Color : existing.Color;

        // 创建新画刷，先启动动画再写入字典
        // （WPF 会在写入 Application.Resources 时冻结 Freezable，之后无法 BeginAnimation）
        var newBrush = new SolidColorBrush(from);

        if (animate && from != target)
        {
            var anim = new ColorAnimation(target, duration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };
            newBrush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }
        else
        {
            newBrush.Color = target;
        }

        // 写入字典放最后，此时动画已附加到画刷上
        res[key] = newBrush;
    }

    private static void ReapplyCurrentTheme()
    {
        if (_currentStyle == ThemeStyle.MaterialYou)
            ApplyMaterialYouTheme(_currentColorScheme);
        else
            ApplyStardewTheme();
    }

    private static ThemeMode ResolveEffectiveThemeMode(ThemeMode mode)
    {
        if (mode != ThemeMode.System)
            return mode;

        try
        {
            var reg = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = reg?.GetValue("AppsUseLightTheme");
            if (value is int intValue)
                return intValue == 0 ? ThemeMode.Dark : ThemeMode.Light;
        }
        catch
        {
        }

        return ThemeMode.Light;
    }

    private static ThemeDefinition AdjustThemeByMode(ThemeDefinition theme, ThemeMode mode)
    {
        if (mode == ThemeMode.Light)
            return theme;

        // ===== 深色模式 =====
        // 文字在深底上需足够亮（WCAG AA ≥ 4.5:1），强调色稍微提亮以保持辨识度
        var accentLight = Lighten(theme.Color2, 0.35);   // 主强调色提亮
        var accentSoft  = Lighten(theme.Color3, 0.20);   // 次强调色

        return new ThemeDefinition
        {
            DisplayName = theme.DisplayName,
            Style = theme.Style,
            ColorScheme = theme.ColorScheme,

            // 主色调
            Color1 = ParseColor("#E6E1E5"),              // 主要文字 - 高对比度浅灰
            Color2 = accentLight,                         // 主强调色 - 提亮后在深底上可读
            Color3 = accentSoft,                          // 次强调色
            Color4 = ParseColor("#2D2B30"),              // 背景浅色 → 深色面板
            Color5 = ParseColor("#1E1C21"),              // 卡片背景 → 深色卡片
            Color6 = Lighten(theme.Color6, 0.30),         // 边框/分割线 - 提亮
            Color7 = Lighten(theme.Color7, 0.25),         // 辅助色
            Color8 = Lighten(theme.Color8, 0.15),         // 高亮色

            // 背景色
            ColorBg0 = ParseColor("#121015"),             // 最深层背景
            ColorBg1 = ParseColor("#1C1B1F"),            // 主背景
            ColorBackground = ParseColor("#141218"),      // 页面底色
            ColorTransparentBackground = ParseColor("#EB1C1B1F"), // 半透明深色卡片

            // 灰度色系 - 确保从亮到暗的梯度在深色背景上可读
            Gray1 = ParseColor("#E6E1E5"),  // 最亮 - 主文本
            Gray2 = ParseColor("#CAC4D0"),  // 次文本
            Gray3 = ParseColor("#938F99"),  // 禁用/辅助
            Gray4 = ParseColor("#79747E"),  // 边框
            Gray5 = ParseColor("#49454F"),  // 分隔符
            Gray6 = ParseColor("#332F35"),  // 深底卡片内边框
            Gray7 = ParseColor("#252329"),  // 下沉面板
            Gray8 = ParseColor("#1C1B1F"),  // 深底 Surface

            // 状态色 - 深色模式下稍微提亮
            ColorSuccess = Lighten(theme.ColorSuccess, 0.15),
            ColorSuccessLight = Lighten(theme.ColorSuccessLight, 0.10),
            ColorRedBack = ParseColor("#40CF6679"),       // 半透明红底
            ColorRedLight = ParseColor("#EF9A9A"),
            ColorRedDark = ParseColor("#EF5350"),         // 深色下红色需更亮

            // 透明度色
            ColorHalfWhite = ParseColor("#30FFFFFF"),
            ColorSemiWhite = ParseColor("#60FFFFFF"),
            ColorSemiTransparent = ParseColor("#08FFFFFF"),
            ColorToolTip = ParseColor("#F0302D31"),       // 深色 tooltip

            // 日志色 - 深底上提亮以保证可读
            ColorFatal = ParseColor("#FF8A80"),           // 亮红
            ColorError = ParseColor("#FF5252"),           // 亮红
            ColorWarn  = ParseColor("#FFD54F"),           // 亮黄
            ColorInfoDark = ParseColor("#90CAF9"),        // 亮蓝
            ColorInfo  = ParseColor("#42A5F5"),           // 蓝
            ColorDebug = ParseColor("#B0BEC5"),           // 浅灰蓝
        };
    }

    #endregion

    #region 辅助方法

    private static Color ParseColor(string hex)
    {
        return (Color)ColorConverter.ConvertFromString(hex);
    }

    private static Color Lighten(Color color, double amount)
    {
        var r = (byte)Math.Min(255, color.R + (255 - color.R) * amount);
        var g = (byte)Math.Min(255, color.G + (255 - color.G) * amount);
        var b = (byte)Math.Min(255, color.B + (255 - color.B) * amount);
        return Color.FromArgb(color.A, r, g, b);
    }

    private static Color Darken(Color color, double amount)
    {
        var factor = Math.Max(0, 1 - amount);
        var r = (byte)Math.Max(0, color.R * factor);
        var g = (byte)Math.Max(0, color.G * factor);
        var b = (byte)Math.Max(0, color.B * factor);
        return Color.FromArgb(color.A, r, g, b);
    }

    private static Color WithAlpha(Color color, double alpha)
    {
        return Color.FromArgb((byte)(alpha * 255), color.R, color.G, color.B);
    }

    #endregion
}
