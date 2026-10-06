using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Panel.Viewer.Wpf;

internal static class Gfx
{
    private static readonly Dictionary<Color, Brush> BrushCache = new();
    private static readonly Dictionary<(Color, double), Pen> PenCache = new();

    public static readonly Typeface Face = new("Segoe UI");

    public static readonly Brush Background = Solid(Color.FromRgb(0x1E, 0x1E, 0x1E));
    public static readonly Brush RowAlt = Solid(Color.FromRgb(0x25, 0x25, 0x26));
    public static readonly Brush Foreground = Solid(Color.FromRgb(0xE0, 0xE0, 0xE0));
    public static readonly Brush Dim = Solid(Color.FromRgb(0x90, 0x90, 0x90));
    public static readonly Pen GridPen = PenOf(Color.FromRgb(0x3A, 0x3A, 0x3A), 1);
    public static readonly Pen SeparatorPen = PenOf(Color.FromRgb(0x50, 0x50, 0x50), 1);

    public static readonly Brush Hatch = CreateHatch();

    public static Brush Solid(Color c)
    {
        lock (BrushCache)
        {
            if (!BrushCache.TryGetValue(c, out var b))
            {
                b = new SolidColorBrush(c);
                b.Freeze();
                BrushCache[c] = b;
            }
            return b;
        }
    }

    public static Pen PenOf(Color c, double thickness)
    {
        lock (PenCache)
        {
            if (!PenCache.TryGetValue((c, thickness), out var p))
            {
                p = new Pen(Solid(c), thickness);
                p.Freeze();
                PenCache[(c, thickness)] = p;
            }
            return p;
        }
    }

    public static Color ParseColor(string? spec, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(spec)) return fallback;
        try { return (Color)ColorConverter.ConvertFromString(spec); }
        catch (FormatException) { return fallback; }
    }

    /// <summary>Black or white, whichever reads better on <paramref name="bg"/>.</summary>
    public static Brush ContrastOn(Color bg)
    {
        var luminance = 0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B;
        return luminance > 140 ? Solid(Colors.Black) : Solid(Colors.White);
    }

    public static FormattedText Text(string text, double size, Brush brush, double ppd, double maxWidth = double.PositiveInfinity)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, size, brush, ppd);
        if (!double.IsInfinity(maxWidth)) ft.MaxTextWidth = Math.Max(1, maxWidth);
        return ft;
    }

    private static Brush CreateHatch()
    {
        var drawing = new GeometryDrawing(
            null,
            new Pen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), 1.5),
            new LineGeometry(new Point(0, 8), new Point(8, 0)));
        var brush = new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            Viewbox = new Rect(0, 0, 8, 8),
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, 8, 8),
            ViewportUnits = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        return brush;
    }
}
