using System.Windows;
using System.Windows.Media;
using PulseIsland.Core;

namespace PulseIsland.Services;

public sealed record ThemePalette(
    AppThemeStyle Style,
    string DisplayName,
    Color SurfaceStart,
    Color SurfaceMid,
    Color SurfaceEnd,
    Color GlassBorderTop,
    Color GlassBorderBottom,
    Color CardBackground,
    Color CardBorder,
    Color ControlBackground,
    Color ControlBorder,
    Color ButtonBackground,
    Color ButtonHoverBackground,
    Color ButtonPressedBackground,
    Color TextPrimary,
    Color TextSecondary,
    Color TextMuted,
    Color SectionHeader,
    Color AccentPrimary,
    Color AccentSecondary,
    Color AccentText,
    Color PopupBackground,
    Color SelectionHighlight,
    Color ScrollThumb,
    Color ScrollThumbHover,
    Color ReflectionTop,
    bool IsLightTheme);

/// <summary>
/// Central theme manager applying all 7 PulseIsland visual styles across Application.Current.Resources
/// so IslandWindow, SettingsWindow, TranslationPopupWindow, and LassoOverlayWindow update seamlessly.
/// </summary>
public static class ThemeService
{
    public static IReadOnlyDictionary<AppThemeStyle, ThemePalette> Palettes { get; } = new Dictionary<AppThemeStyle, ThemePalette>
    {
        [AppThemeStyle.Classic] = new(
            Style: AppThemeStyle.Classic,
            DisplayName: "Classic (Obsidian / Sky)",
            SurfaceStart: C("#F60B0C11"),
            SurfaceMid: C("#FA11131B"),
            SurfaceEnd: C("#F6090A0E"),
            GlassBorderTop: C("#2CFFFFFF"),
            GlassBorderBottom: C("#12FFFFFF"),
            CardBackground: C("#14FFFFFF"),
            CardBorder: C("#22FFFFFF"),
            ControlBackground: C("#20080A12"),
            ControlBorder: C("#32FFFFFF"),
            ButtonBackground: C("#1EFFFFFF"),
            ButtonHoverBackground: C("#32FFFFFF"),
            ButtonPressedBackground: C("#44FFFFFF"),
            TextPrimary: C("#FFF8FAFF"),
            TextSecondary: C("#CCB8C7E8"),
            TextMuted: C("#88FFFFFF"),
            SectionHeader: C("#FF93C5FD"),
            AccentPrimary: C("#FF38BDF8"),
            AccentSecondary: C("#FFA78BFA"),
            AccentText: C("#FF070910"),
            PopupBackground: C("#F710131E"),
            SelectionHighlight: C("#3438BDF8"),
            ScrollThumb: C("#38FFFFFF"),
            ScrollThumbHover: C("#62FFFFFF"),
            ReflectionTop: C("#20FFFFFF"),
            IsLightTheme: false),

        [AppThemeStyle.CrimsonBlack] = new(
            Style: AppThemeStyle.CrimsonBlack,
            DisplayName: "Crimson / Black",
            SurfaceStart: C("#F80A0507"),
            SurfaceMid: C("#FA18090E"),
            SurfaceEnd: C("#F8060405"),
            GlassBorderTop: C("#44FB7185"),
            GlassBorderBottom: C("#1C991B1B"),
            CardBackground: C("#16FF2E4D"),
            CardBorder: C("#34FB7185"),
            ControlBackground: C("#3014060A"),
            ControlBorder: C("#42FB7185"),
            ButtonBackground: C("#24FF3B5C"),
            ButtonHoverBackground: C("#3EFF4D6D"),
            ButtonPressedBackground: C("#55EF4444"),
            TextPrimary: C("#FFFFF5F6"),
            TextSecondary: C("#CCFCA5A5"),
            TextMuted: C("#99FDA4AF"),
            SectionHeader: C("#FFFB7185"),
            AccentPrimary: C("#FFEF4444"),
            AccentSecondary: C("#FF991B1B"),
            AccentText: C("#FFFFFFFF"),
            PopupBackground: C("#F716080C"),
            SelectionHighlight: C("#3CEF4444"),
            ScrollThumb: C("#48FB7185"),
            ScrollThumbHover: C("#78FB7185"),
            ReflectionTop: C("#1EFF6B81"),
            IsLightTheme: false),

        [AppThemeStyle.IndigoAmber] = new(
            Style: AppThemeStyle.IndigoAmber,
            DisplayName: "Indigo / Amber",
            SurfaceStart: C("#F60D0F24"),
            SurfaceMid: C("#FA161938"),
            SurfaceEnd: C("#F60A0C1B"),
            GlassBorderTop: C("#48FBBF24"),
            GlassBorderBottom: C("#246366F1"),
            CardBackground: C("#1A6366F1"),
            CardBorder: C("#38818CF8"),
            ControlBackground: C("#2E0E1028"),
            ControlBorder: C("#40818CF8"),
            ButtonBackground: C("#226366F1"),
            ButtonHoverBackground: C("#3A818CF8"),
            ButtonPressedBackground: C("#4CF59E0B"),
            TextPrimary: C("#FFF8FAFF"),
            TextSecondary: C("#CCFDE68A"),
            TextMuted: C("#99C7D2FE"),
            SectionHeader: C("#FFFBBF24"),
            AccentPrimary: C("#FFF59E0B"),
            AccentSecondary: C("#FF6366F1"),
            AccentText: C("#FF0B0B14"),
            PopupBackground: C("#F7141734"),
            SelectionHighlight: C("#3AF59E0B"),
            ScrollThumb: C("#48FBBF24"),
            ScrollThumbHover: C("#78F59E0B"),
            ReflectionTop: C("#1EFDE68A"),
            IsLightTheme: false),

        [AppThemeStyle.NavyMagenta] = new(
            Style: AppThemeStyle.NavyMagenta,
            DisplayName: "Deep Navy / Magenta",
            SurfaceStart: C("#F6071124"),
            SurfaceMid: C("#FA0D1C3B"),
            SurfaceEnd: C("#F6060D1C"),
            GlassBorderTop: C("#44F472B6"),
            GlassBorderBottom: C("#223B82F6"),
            CardBackground: C("#183B82F6"),
            CardBorder: C("#36EC4899"),
            ControlBackground: C("#2E08132B"),
            ControlBorder: C("#4060A5FA"),
            ButtonBackground: C("#223B82F6"),
            ButtonHoverBackground: C("#3AEC4899"),
            ButtonPressedBackground: C("#55EC4899"),
            TextPrimary: C("#FFF8FAFF"),
            TextSecondary: C("#CCF9A8D4"),
            TextMuted: C("#9993C5FD"),
            SectionHeader: C("#FFF472B6"),
            AccentPrimary: C("#FFEC4899"),
            AccentSecondary: C("#FF3B82F6"),
            AccentText: C("#FFFFFFFF"),
            PopupBackground: C("#F70B1732"),
            SelectionHighlight: C("#3AEC4899"),
            ScrollThumb: C("#48F472B6"),
            ScrollThumbHover: C("#78EC4899"),
            ReflectionTop: C("#1EF472B6"),
            IsLightTheme: false),

        [AppThemeStyle.GraphiteSand] = new(
            Style: AppThemeStyle.GraphiteSand,
            DisplayName: "Graphite / Sand",
            SurfaceStart: C("#F6161619"),
            SurfaceMid: C("#FA222227"),
            SurfaceEnd: C("#F6121215"),
            GlassBorderTop: C("#40E5C98B"),
            GlassBorderBottom: C("#1AFFFFFF"),
            CardBackground: C("#16E5C98B"),
            CardBorder: C("#30D6C39A"),
            ControlBackground: C("#2A18181C"),
            ControlBorder: C("#3CD6C39A"),
            ButtonBackground: C("#1EE5C98B"),
            ButtonHoverBackground: C("#36E5C98B"),
            ButtonPressedBackground: C("#4CD9B36C"),
            TextPrimary: C("#FFFAF7F0"),
            TextSecondary: C("#CCD6C39A"),
            TextMuted: C("#99A8A29E"),
            SectionHeader: C("#FFE5C98B"),
            AccentPrimary: C("#FFD9B36C"),
            AccentSecondary: C("#FF78716C"),
            AccentText: C("#FF14120E"),
            PopupBackground: C("#F71E1E23"),
            SelectionHighlight: C("#38D9B36C"),
            ScrollThumb: C("#44D6C39A"),
            ScrollThumbHover: C("#74E5C98B"),
            ReflectionTop: C("#1AFDE68A"),
            IsLightTheme: false),

        [AppThemeStyle.TealOrange] = new(
            Style: AppThemeStyle.TealOrange,
            DisplayName: "Teal / Orange",
            SurfaceStart: C("#F6061A1D"),
            SurfaceMid: C("#FA0A262B"),
            SurfaceEnd: C("#F6051417"),
            GlassBorderTop: C("#442DD4BF"),
            GlassBorderBottom: C("#28F97316"),
            CardBackground: C("#1814B8A6"),
            CardBorder: C("#362DD4BF"),
            ControlBackground: C("#2E061B1E"),
            ControlBorder: C("#3E2DD4BF"),
            ButtonBackground: C("#2214B8A6"),
            ButtonHoverBackground: C("#3AF97316"),
            ButtonPressedBackground: C("#55F97316"),
            TextPrimary: C("#FFF2FFFD"),
            TextSecondary: C("#CCFDBA74"),
            TextMuted: C("#995EEAD4"),
            SectionHeader: C("#FFFB923C"),
            AccentPrimary: C("#FFF97316"),
            AccentSecondary: C("#FF14B8A6"),
            AccentText: C("#FFFFFFFF"),
            PopupBackground: C("#F7092226"),
            SelectionHighlight: C("#3AF97316"),
            ScrollThumb: C("#482DD4BF"),
            ScrollThumbHover: C("#78F97316"),
            ReflectionTop: C("#1E5EEAD4"),
            IsLightTheme: false),

        [AppThemeStyle.LightWhite] = new(
            Style: AppThemeStyle.LightWhite,
            DisplayName: "Light Frosted Glass",
            SurfaceStart: C("#F5F8FAFC"),
            SurfaceMid: C("#FAFFFFFF"),
            SurfaceEnd: C("#F5EEF2F7"),
            GlassBorderTop: C("#380F172A"),
            GlassBorderBottom: C("#220F172A"),
            CardBackground: C("#D8FFFFFF"),
            CardBorder: C("#220F172A"),
            ControlBackground: C("#EEFFFFFF"),
            ControlBorder: C("#300F172A"),
            ButtonBackground: C("#140F172A"),
            ButtonHoverBackground: C("#242563EB"),
            ButtonPressedBackground: C("#362563EB"),
            TextPrimary: C("#FF0F172A"),
            TextSecondary: C("#FF475569"),
            TextMuted: C("#FF64748B"),
            SectionHeader: C("#FF2563EB"),
            AccentPrimary: C("#FF2563EB"),
            AccentSecondary: C("#FF60A5FA"),
            AccentText: C("#FFFFFFFF"),
            PopupBackground: C("#FAFFFFFF"),
            SelectionHighlight: C("#282563EB"),
            ScrollThumb: C("#400F172A"),
            ScrollThumbHover: C("#6A0F172A"),
            ReflectionTop: C("#55FFFFFF"),
            IsLightTheme: true)
    };

    public static AppThemeStyle CurrentStyle { get; private set; } = AppThemeStyle.Classic;
    public static event Action<AppThemeStyle>? ThemeChanged;

    public static ThemePalette GetPalette(AppThemeStyle style)
        => Palettes.TryGetValue(style, out var p) ? p : Palettes[AppThemeStyle.Classic];

    public static string GetLocalizedDisplayName(AppThemeStyle style)
        => LocalizationService.Get($"Loc_Theme_{style}");

    public static void ApplyTheme(AppThemeStyle style)
    {
        CurrentStyle = style;
        var app = Application.Current;
        if (app is not null)
        {
            var p = GetPalette(style);
            var res = app.Resources;

            res["ThemeSurfaceBrush"] = CreateGrad(p.SurfaceStart, p.SurfaceMid, p.SurfaceEnd, new Point(0, 0), new Point(1, 1));
            res["ThemeGlassBorderBrush"] = CreateGrad(p.GlassBorderTop, p.GlassBorderBottom, null, new Point(0, 0), new Point(0, 1));
            res["ThemeAccentGradientBrush"] = CreateGrad(p.AccentPrimary, p.AccentSecondary, null, new Point(0, 0), new Point(1, 0));
            res["ThemeReflectionBrush"] = CreateGrad(p.ReflectionTop, Color.FromArgb(0, 255, 255, 255), null, new Point(0, 0), new Point(0, 1));

            res["ThemeCardBgBrush"] = FreezeBrush(p.CardBackground);
            res["ThemeCardBorderBrush"] = FreezeBrush(p.CardBorder);
            res["ThemeControlBgBrush"] = FreezeBrush(p.ControlBackground);
            res["ThemeControlBorderBrush"] = FreezeBrush(p.ControlBorder);
            res["ThemeButtonBgBrush"] = FreezeBrush(p.ButtonBackground);
            res["ThemeButtonHoverBgBrush"] = FreezeBrush(p.ButtonHoverBackground);
            res["ThemeButtonPressedBgBrush"] = FreezeBrush(p.ButtonPressedBackground);
            res["ThemeTextPrimaryBrush"] = FreezeBrush(p.TextPrimary);
            res["ThemeTextSecondaryBrush"] = FreezeBrush(p.TextSecondary);
            res["ThemeTextMutedBrush"] = FreezeBrush(p.TextMuted);
            res["ThemeSectionHeaderBrush"] = FreezeBrush(p.SectionHeader);
            res["ThemeAccentBrush"] = FreezeBrush(p.AccentPrimary);
            res["ThemeAccentSecondaryBrush"] = FreezeBrush(p.AccentSecondary);
            res["ThemeAccentTextBrush"] = FreezeBrush(p.AccentText);
            res["ThemePopupBgBrush"] = FreezeBrush(p.PopupBackground);
            res["ThemeHighlightBrush"] = FreezeBrush(p.SelectionHighlight);
            res["ThemeScrollThumbBrush"] = FreezeBrush(p.ScrollThumb);
            res["ThemeScrollThumbHoverBrush"] = FreezeBrush(p.ScrollThumbHover);
            res["ThemeScrollTrackBrush"] = FreezeBrush(p.IsLightTheme ? Color.FromArgb(0x14, 0x0F, 0x17, 0x2A) : Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
            res["ThemeSwitchTrackOffBrush"] = FreezeBrush(p.IsLightTheme ? Color.FromArgb(0x28, 0x0F, 0x17, 0x2A) : Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
            res["ThemeSwitchThumbBrush"] = FreezeBrush(Colors.White);
            res["ThemeShadowColor"] = p.IsLightTheme ? Color.FromArgb(0x38, 0x0F, 0x17, 0x2A) : Color.FromArgb(0x75, 0x00, 0x00, 0x00);
        }

        ThemeChanged?.Invoke(style);
    }

    private static SolidColorBrush FreezeBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static LinearGradientBrush CreateGrad(Color c0, Color c1, Color? c2, Point start, Point end)
    {
        var g = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        g.GradientStops.Add(new GradientStop(c0, 0.0));
        if (c2 is not null)
        {
            g.GradientStops.Add(new GradientStop(c1, 0.5));
            g.GradientStops.Add(new GradientStop(c2.Value, 1.0));
        }
        else
        {
            g.GradientStops.Add(new GradientStop(c1, 1.0));
        }
        g.Freeze();
        return g;
    }

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
