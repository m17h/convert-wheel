using System.Windows;
using System.Windows.Media;
using ConvertWheel.Conversion;

namespace ConvertWheel.UI;

/// <summary>The wheel's colours: its own dark glass, or the user's colours when Windows is in High Contrast.</summary>
internal sealed class WheelTheme
{
    public bool HighContrast { get; private init; }
    public required Brush InnerDisc { get; init; }
    public required Brush OuterDisc { get; init; }
    public required Brush DiscEdge { get; init; }
    public required Brush Hub { get; init; }
    public required Brush HubEdge { get; init; }
    public required Brush Tile { get; init; }
    public required Brush? TileEdge { get; init; }
    public required Brush Text { get; init; }
    public required Brush SubtleText { get; init; }
    public required Brush HotText { get; init; }
    public required Brush Notice { get; init; }
    public required double TileOpacity { get; init; }

    Brush? _hotFill, _accent;

    /// <summary>The fill of a chosen slice, always dark enough (or light enough) for <see cref="HotText"/>.</summary>
    public Brush HotFill(FormatKind? kind) => _hotFill ?? Palette.Gradient(kind is { } k ? Palette.Deep(k) : Palette.DeepMore);

    /// <summary>The coloured edge that tells the kinds apart.</summary>
    public Brush Accent(FormatKind kind) => _accent ?? Palette.Gradient(Palette.Accent(kind));

    /// <summary>The chosen format's name in the middle.</summary>
    public Brush HubAccent(FormatKind kind) => _accent ?? Palette.Gradient(Palette.Bright(kind));

    public static readonly WheelTheme Dark = new()
    {
        InnerDisc = Radial("#F21A1A21", "#EB0F0F13"),
        OuterDisc = Radial("#E6141419", "#DC0D0D11"),
        DiscEdge = Palette.Freeze(new LinearGradientBrush(Color.FromArgb(70, 255, 255, 255), Color.FromArgb(14, 255, 255, 255), 90)),
        Hub = Palette.Freeze(new RadialGradientBrush(Palette.Hex("#2E2E39"), Palette.Hex("#1A1A21")) { GradientOrigin = new Point(0.5, 0.25) }),
        HubEdge = Palette.Freeze(new LinearGradientBrush(Color.FromArgb(90, 255, 255, 255), Color.FromArgb(18, 255, 255, 255), 90)),
        Tile = Palette.Solid("#2A2A34"),
        TileEdge = null,
        Text = Palette.Solid("#F2F2F7"),
        SubtleText = Palette.Solid("#B0B0BE"),
        HotText = Brushes.White,
        Notice = Palette.Solid("#FBBF24"),
        TileOpacity = 0.92,
    };

    public static WheelTheme Contrast(Color background, Color text, Color highlight, Color highlightText)
    {
        var back = Solid(background);
        var fore = Solid(text);
        return new WheelTheme
        {
            HighContrast = true,
            InnerDisc = back,
            OuterDisc = back,
            DiscEdge = fore,
            Hub = back,
            HubEdge = fore,
            Tile = back,
            TileEdge = fore,
            Text = fore,
            SubtleText = fore,
            HotText = Solid(highlightText),
            Notice = fore,
            TileOpacity = 1,
            _hotFill = Solid(highlight),
            _accent = fore,
        };
    }

    /// <summary>The theme for how Windows is set up right now.</summary>
    public static WheelTheme Current => SystemParameters.HighContrast
        ? Contrast(SystemColors.WindowColor, SystemColors.WindowTextColor, SystemColors.HighlightColor, SystemColors.HighlightTextColor)
        : Dark;

    static Brush Radial(string inner, string outer) => Palette.Freeze(new RadialGradientBrush(Palette.Hex(inner), Palette.Hex(outer)));

    static Brush Solid(Color color) => Palette.Freeze(new SolidColorBrush(color));
}
