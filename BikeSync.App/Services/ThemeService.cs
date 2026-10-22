using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace BikeSync.App.Services;

public sealed record ThemePalette(
    string Id,
    string Name,
    string Description,
    string AccentHex,
    string AccentTextHex,
    string BackgroundHex,
    string SidebarHex,
    string SurfaceHex,
    string SurfaceAltHex,
    string SurfaceRaisedHex,
    string BorderHex,
    string BorderSoftHex,
    string NavSelectedHex,
    string NavHoverHex,
    string InputBackgroundHex,
    string AccentHoverHex,
    string AccentPressedHex,
    string GradientStartHex,
    string GradientEndHex,
    string HeroStartHex,
    string HeroMidHex,
    string HeroEndHex)
{
    public string PreviewHex => SurfaceAltHex;
}

/// <summary>
/// Applies and switches the application color theme at runtime. Every themed brush
/// is replaced in the application resources, so DynamicResource usages update live.
/// </summary>
public sealed class ThemeService
{
    private static readonly ThemePalette[] Palettes =
    {
        new(
            "ember", "熔岩橙", "暖橙点缀 · 默认",
            "#FF6B2C", "#FFB38F", "#0A0F16", "#0D141D", "#121B26", "#0F1721", "#172231",
            "#223144", "#1A2635", "#1A2634", "#121C28", "#0E1620",
            "#FF814D", "#E9581C", "#FF8A4C", "#FF5A1F",
            "#241710", "#141D28", "#101720"),
        new(
            "ice", "冰川蓝", "冷蓝点缀 · 清爽",
            "#4C8DFF", "#9CC2FF", "#080E18", "#0B1320", "#101B2C", "#0D1724", "#162338",
            "#20344F", "#182940", "#172A42", "#111E30", "#0C1726",
            "#6BA2FF", "#3A78E0", "#6CA8FF", "#3E7BFF",
            "#101A2B", "#0F1A29", "#0B1420"),
        new(
            "forest", "森林绿", "绿色点缀 · 自然",
            "#34D399", "#8BEBC4", "#08110E", "#0B1613", "#101D18", "#0D1915", "#16271F",
            "#213D31", "#193026", "#18362A", "#11251C", "#0C1A15",
            "#5BE3B0", "#23B77F", "#5BE3B0", "#27C98D",
            "#0F2119", "#0C1B16", "#091410"),
        new(
            "violet", "暮光紫", "紫色点缀 · 静谧",
            "#A78BFA", "#CBB8FF", "#0B0A14", "#100E1C", "#171427", "#131120", "#201B34",
            "#332B4C", "#282240", "#2A2442", "#1B1830", "#121020",
            "#BD9CFF", "#8E6FE0", "#C0A4FF", "#9A78F5",
            "#1B1630", "#151228", "#100D1E"),
    };

    public IReadOnlyList<ThemePalette> Available => Palettes;

    public string CurrentId { get; private set; } = "ember";

    public static string Normalize(string? themeId)
    {
        if (string.IsNullOrWhiteSpace(themeId))
        {
            return "ember";
        }

        var value = themeId.Trim();
        return Palettes.Any(p => string.Equals(p.Id, value, StringComparison.OrdinalIgnoreCase))
            ? Palettes.First(p => string.Equals(p.Id, value, StringComparison.OrdinalIgnoreCase)).Id
            : "ember";
    }

    public void Apply(string? themeId)
    {
        var id = Normalize(themeId);
        var palette = Palettes.First(p => p.Id == id);
        CurrentId = palette.Id;

        ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.None, false);
        var accent = Parse(palette.AccentHex);
        ApplicationAccentColorManager.Apply(accent, ApplicationTheme.Dark, false, false);

        var surface = Parse(palette.SurfaceHex);
        SetBrush("AppBackgroundBrush", Parse(palette.BackgroundHex));
        SetBrush("SidebarBrush", Parse(palette.SidebarHex));
        SetBrush("SurfaceBrush", surface);
        SetBrush("SurfaceAltBrush", Parse(palette.SurfaceAltHex));
        SetBrush("SurfaceRaisedBrush", Parse(palette.SurfaceRaisedHex));
        SetBrush("CardBrush", surface);
        SetBrush("CardBrushSoft", WithAlpha(surface, 0xB3));
        SetBrush("BorderBrushCustom", Parse(palette.BorderHex));
        SetBrush("BorderSoftBrush", Parse(palette.BorderSoftHex));
        SetBrush("NavSelectedBrush", Parse(palette.NavSelectedHex));
        SetBrush("NavHoverBrush", Parse(palette.NavHoverHex));
        SetBrush("InputBackgroundBrush", Parse(palette.InputBackgroundHex));
        SetBrush("AccentBrush", accent);
        SetBrush("AccentSoftBrush", WithAlpha(accent, 0x26));
        SetBrush("AccentTintBrush", WithAlpha(accent, 0x1A));
        SetBrush("AccentBorderBrush", WithAlpha(accent, 0x33));
        SetBrush("AccentSelectionBrush", WithAlpha(accent, 0x1F));
        SetBrush("AccentHoverBrush", Parse(palette.AccentHoverHex));
        SetBrush("AccentPressedBrush", Parse(palette.AccentPressedHex));
        SetBrush("AccentTextBrush", Parse(palette.AccentTextHex));

        SetGradient(
            "AccentGradientBrush",
            Parse(palette.GradientStartHex),
            Parse(palette.GradientEndHex));
        SetGradient(
            "HeroGradientBrush",
            Parse(palette.HeroStartHex),
            Parse(palette.HeroMidHex),
            Parse(palette.HeroEndHex));
    }

    private static void SetBrush(string key, Color color)
    {
        if (Application.Current is not { } application)
        {
            return;
        }

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        application.Resources[key] = brush;
    }

    private static void SetGradient(string key, Color start, Color end)
    {
        if (Application.Current is not { } application)
        {
            return;
        }

        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops =
            {
                new GradientStop(start, 0),
                new GradientStop(end, 1),
            },
        };
        brush.Freeze();
        application.Resources[key] = brush;
    }

    private static void SetGradient(string key, Color start, Color middle, Color end)
    {
        if (Application.Current is not { } application)
        {
            return;
        }

        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops =
            {
                new GradientStop(start, 0),
                new GradientStop(middle, 0.58),
                new GradientStop(end, 1),
            },
        };
        brush.Freeze();
        application.Resources[key] = brush;
    }

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);
}
