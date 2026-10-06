using System.Windows;

namespace SnapAnchor.Models;

internal enum AnnotationKind
{
    Rectangle,
    Ellipse,
    Line,
    Arrow,
    Pencil,
    Marker,
    Text,
    Number,
    Callout,
    Mosaic,
    Blur,
    Magnify,
    // Appended to preserve the numeric values of annotation kinds already
    // stored in existing history documents.
    Eraser
}

internal sealed class AnnotationItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public AnnotationKind Kind { get; init; }
    public List<Point> Points { get; set; } = [];
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public string Color { get; set; } = "#FFEF4444";
    public double Thickness { get; set; } = 4;
    public string Text { get; set; } = string.Empty;
    public double Opacity { get; set; } = 1;
    public double FillOpacity { get; set; }
    public bool Dashed { get; set; }
    public string ArrowHeads { get; set; } = "End";
    public string EffectShape { get; set; } = "Rectangle";
    public double Rotation { get; set; }
    public string FontFamily { get; set; } = "Segoe UI";
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    public bool Strikethrough { get; set; }
    public string TextAlignment { get; set; } = "Left";
    public bool Outline { get; set; }

    internal bool VisuallyEquals(AnnotationItem other) => Kind == other.Kind && X == other.X && Y == other.Y &&
        Width == other.Width && Height == other.Height && Color == other.Color && Thickness == other.Thickness &&
        Text == other.Text && Opacity == other.Opacity && FillOpacity == other.FillOpacity && Dashed == other.Dashed &&
        ArrowHeads == other.ArrowHeads && EffectShape == other.EffectShape && Rotation == other.Rotation &&
        FontFamily == other.FontFamily && Bold == other.Bold && Italic == other.Italic && Underline == other.Underline &&
        Strikethrough == other.Strikethrough && TextAlignment == other.TextAlignment && Outline == other.Outline &&
        Points.SequenceEqual(other.Points);

    public AnnotationItem Clone() => new()
    {
        Id = Id,
        Kind = Kind,
        Points = Points.ToList(),
        X = X,
        Y = Y,
        Width = Width,
        Height = Height,
        Color = Color,
        Thickness = Thickness,
        Text = Text,
        Opacity = Opacity,
        FillOpacity = FillOpacity,
        Dashed = Dashed,
        ArrowHeads = ArrowHeads,
        EffectShape = EffectShape,
        Rotation = Rotation,
        FontFamily = FontFamily,
        Bold = Bold,
        Italic = Italic,
        Underline = Underline,
        Strikethrough = Strikethrough,
        TextAlignment = TextAlignment,
        Outline = Outline
    };
}

internal sealed class AnnotationDocument
{
    public int Version { get; set; } = 1;
    public List<AnnotationItem> Items { get; set; } = [];
}
