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

    /// <summary>The slides to draw: hidden ones are left out, as PowerPoint does, unless that leaves nothing.</summary>
    public List<Slide> Shown => Slides.Any(s => !s.Hidden) ? Slides.Where(s => !s.Hidden).ToList() : Slides;
}

internal sealed class Slide
{
    /// <summary>Skipped when presenting.</summary>
    public bool Hidden { get; set; }
    public SlideFill? Background { get; set; }
    /// <summary>Speaker notes, one string per paragraph.</summary>
    public List<string> Notes { get; } = [];
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
    /// <summary>Fractions of the picture left outside each side of the shape (negative: room between them).</summary>
    public Thickness Crop { get; set; }

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

internal enum ArrowHead { None, Triangle, Stealth, Open, Diamond, Oval }

/// <summary>One side of a table cell; no colour means no line.</summary>
internal sealed record CellEdge(double Width, Color? Color);

/// <summary>How a slide's table cell looks where it differs from its table.</summary>
internal sealed class CellLook
{
    public TextAnchor Anchor { get; set; }
    public Thickness? Padding { get; set; }
    public CellEdge? Left { get; set; }
    public CellEdge? Top { get; set; }
    public CellEdge? Right { get; set; }
    public CellEdge? Bottom { get; set; }
}

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
    /// <summary>Fractions cropped from each side of the picture (negative: empty room on that side).</summary>
    public Thickness Crop { get; set; }
    /// <summary>Picture opacity, 0..1.</summary>
    public double PictureOpacity { get; set; } = 1;

    /// <summary>Text (paragraphs and tables) laid out inside the shape.</summary>
    public List<DBlock> Text { get; } = [];
    public Thickness Insets { get; set; } = new(7.2, 3.6, 7.2, 3.6);
    public TextAnchor Anchor { get; set; } = TextAnchor.Top;
    public bool Wrap { get; set; } = true;
    /// <summary>Shrink the text until it fits, as PowerPoint's "shrink text on overflow" does.</summary>
    public bool ShrinkToFit { get; set; }
    /// <summary>Text turned within the shape: a quarter turn ("vert" / "vert270") or upside down (180).</summary>
    public int TextRotation { get; set; }

    /// <summary>The role of a placeholder ("title", "body"…), kept so writers can mark titles.</summary>
    public string? Role { get; set; }

    public bool HasText => Text.Any(b => b is DTable || b is DParagraph p && p.Inlines.Count > 0);

    /// <summary>Moves the shape by a transform (a group's flip and turn): its place, its own turn and its own flip.</summary>
    public void Transform(Matrix m)
    {
        var own = Matrix.Identity;
        own.Scale(FlipH ? -1 : 1, FlipV ? -1 : 1);
        own.Rotate(Rotation);
        var total = own * m;
        Vector across = total.Transform(new Vector(1, 0)), down = total.Transform(new Vector(0, 1));
        var center = m.Transform(new Point(Bounds.X + Bounds.Width / 2, Bounds.Y + Bounds.Height / 2));
        double w = Bounds.Width * across.Length, h = Bounds.Height * down.Length;
        // Every combination of flips comes down to a turn, with or without one mirroring.
        FlipH = total.Determinant < 0;
        FlipV = false;
        if (FlipH) across.Negate();
        Rotation = Math.Round(Math.Atan2(across.Y, across.X) * 180 / Math.PI, 6);
        if (Path != null && (Math.Abs(across.Length - 1) > 0.001 || Math.Abs(down.Length - 1) > 0.001))
        {
            Path = Path.Clone();
            Path.Transform = new MatrixTransform((Path.Transform?.Value ?? Matrix.Identity) * new Matrix(across.Length, 0, 0, down.Length, 0, 0));
        }
        Bounds = new Rect(center.X - w / 2, center.Y - h / 2, w, h);
    }

    /// <summary>Where the picture goes and which part of it shows, once cropping (inwards or outwards) is worked out.</summary>
    public static (Rect area, Thickness crop) PictureArea(Rect r, Thickness crop)
    {
        double across = 1 - crop.Left - crop.Right, down = 1 - crop.Top - crop.Bottom;
        if (crop == default || across <= 0.001 || down <= 0.001) return (r, default);
        double left = Math.Max(0, -crop.Left) / across, right = Math.Max(0, -crop.Right) / across;
        double top = Math.Max(0, -crop.Top) / down, bottom = Math.Max(0, -crop.Bottom) / down;
        var area = new Rect(r.X + left * r.Width, r.Y + top * r.Height, Math.Max(0, 1 - left - right) * r.Width, Math.Max(0, 1 - top - bottom) * r.Height);
        return (area, new Thickness(Math.Max(0, crop.Left), Math.Max(0, crop.Top), Math.Max(0, crop.Right), Math.Max(0, crop.Bottom)));
    }
}
