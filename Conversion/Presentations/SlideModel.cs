using System.Windows;
using System.Windows.Media;
using ConvertWheel.Conversion.Documents;

namespace ConvertWheel.Conversion.Presentations;

/// <summary>A slide deck as positioned shapes: what PPTX and ODP have in common. Sizes are in points.</summary>
internal sealed class Deck
{
    public double Width { get; set; } = 720;
    public double Height { get; set; } = 405;
    public string? Title { get; set; }
    public string Font { get; set; } = "Calibri";
    public List<Slide> Slides { get; } = [];
}

internal sealed class Slide
{
    public SlideFill? Background { get; set; }
    /// <summary>Bottom to top: master and layout decorations first, then the slide's own shapes.</summary>
    public List<SlideShape> Shapes { get; } = [];
}

/// <summary>A solid colour, a gradient or a picture.</summary>
internal sealed class SlideFill
{
    public Color? Color { get; set; }
    public List<(double Position, Color Color)>? Gradient { get; set; }
    /// <summary>Direction of a linear gradient in degrees, clockwise from left-to-right.</summary>
    public double GradientAngle { get; set; }
    public bool RadialGradient { get; set; }
    public DImage? Image { get; set; }
    /// <summary>Repeat the picture at this scale (1 = natural size) instead of stretching it.</summary>
    public double? Tile { get; set; }
    /// <summary>Picture opacity, 0..1.</summary>
    public double Opacity { get; set; } = 1;

    public static SlideFill Solid(Color c) => new() { Color = c };

    /// <summary>One colour standing in for the fill, for formats that can't express it.</summary>
    public Color? Average => Color ?? (Gradient is { Count: > 0 } g
        ? System.Windows.Media.Color.FromArgb(
            (byte)g.Average(s => s.Color.A), (byte)g.Average(s => s.Color.R), (byte)g.Average(s => s.Color.G), (byte)g.Average(s => s.Color.B))
        : null);
}

internal enum ShapeGeometry
{
    Rect, RoundRect, Ellipse, Triangle, RightTriangle, Diamond, Parallelogram, Trapezoid, Pentagon, Hexagon, Octagon,
    Chevron, HomePlate, RightArrow, LeftArrow, UpArrow, DownArrow, Star5, Plus, Line, Custom,
    /// <summary>Connectors: an elbow (across, down, across), a single corner, and an S-curve.</summary>
    Elbow, Corner, Curve,
}

internal enum TextAnchor { Top, Middle, Bottom }

internal enum ArrowHead { None, Triangle }

internal sealed class SlideShape
{
    public Rect Bounds { get; set; }
    /// <summary>Clockwise degrees about the centre.</summary>
    public double Rotation { get; set; }
    public bool FlipH { get; set; }
    public bool FlipV { get; set; }
    public ShapeGeometry Geometry { get; set; } = ShapeGeometry.Rect;
    /// <summary>For <see cref="ShapeGeometry.Custom"/>: the outline in shape-local points (0..width, 0..height).</summary>
    public Geometry? Path { get; set; }
    /// <summary>Preset adjustment as a fraction of the shorter side (corner radius, arrow head…); null for the default.</summary>
    public double? Adjust { get; set; }

    public SlideFill? Fill { get; set; }
    public Color? Line { get; set; }
    public double LineWidth { get; set; } = 1;
    public bool Dashed { get; set; }
    public ArrowHead HeadEnd { get; set; }
    public ArrowHead TailEnd { get; set; }

    public DImage? Picture { get; set; }
    /// <summary>Fractions cropped from each side of the picture.</summary>
    public Thickness Crop { get; set; }

    /// <summary>Text (paragraphs and tables) laid out inside the shape.</summary>
    public List<DBlock> Text { get; } = [];
    public Thickness Insets { get; set; } = new(7.2, 3.6, 7.2, 3.6);
    public TextAnchor Anchor { get; set; } = TextAnchor.Top;
    public bool Wrap { get; set; } = true;
    /// <summary>Shrink the text until it fits, as PowerPoint's "shrink text on overflow" does.</summary>
    public bool ShrinkToFit { get; set; }
    /// <summary>Text rotated a quarter turn ("vert" / "vert270").</summary>
    public int TextRotation { get; set; }

    /// <summary>The role of a placeholder ("title", "body"…), kept so writers can mark titles.</summary>
    public string? Role { get; set; }

    public bool HasText => Text.Any(b => b is DTable || b is DParagraph p && p.Inlines.Count > 0);
}
