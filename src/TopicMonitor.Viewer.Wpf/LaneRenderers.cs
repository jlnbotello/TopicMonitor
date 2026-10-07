using System.Windows;
using System.Windows.Media;
using TopicMonitor.Client;
using TopicMonitor.Contracts;
using TopicMonitor.Viewer.Layout;

namespace TopicMonitor.Viewer.Wpf;

/// <summary>Maps server ticks to x positions of the plot area.</summary>
internal readonly record struct TimeMap(long Start, long End, double Left, double Width)
{
    public double X(long t) => Left + (t - Start) * Width / (End - Start);

    public long T(double x) => Start + (long)((x - Left) * (End - Start) / Width);
}

internal sealed record RenderContext(DrawingContext Dc, TimeMap Map, LayoutModel Layout, double Ppd);

internal static class LaneRenderers
{
    private const float LowConfidence = 0.5f;
    private static readonly Color InvalidGrey = Color.FromRgb(0x88, 0x88, 0x88);
    private static readonly Color DefaultLine = Color.FromRgb(0x4F, 0xC3, 0xF7);
    private static readonly Color[] ComponentColors =
    {
        Color.FromRgb(0xEF, 0x53, 0x50), Color.FromRgb(0x66, 0xBB, 0x6A), Color.FromRgb(0x42, 0xA5, 0xF5),
        Color.FromRgb(0xFF, 0xCA, 0x28), Color.FromRgb(0xAB, 0x47, 0xBC),
    };

    public static void Draw(RenderContext c, LaneModel lane, IReadOnlyList<HistoryPoint> pts, Rect r)
    {
        switch (lane.Renderer)
        {
            case RendererKind.Boxes: DrawBoxes(c, lane, pts, r); break;
            case RendererKind.Step: DrawLines(c, lane, pts, r, singleValue: true); break;
            case RendererKind.Lines: DrawLines(c, lane, pts, r, singleValue: false); break;
            case RendererKind.Digital: DrawDigital(c, lane, pts, r); break;
            case RendererKind.Swatch: DrawSwatch(c, lane, pts, r); break;
        }
    }

    /// <summary>The point in effect at <paramref name="t"/> (last point at or before it), or null.</summary>
    public static HistoryPoint? FindAt(IReadOnlyList<HistoryPoint> pts, long t)
    {
        var i = LastAtOrBefore(pts, t);
        return i < 0 ? null : pts[i];
    }

    private static int LastAtOrBefore(IReadOnlyList<HistoryPoint> pts, long t)
    {
        int lo = 0, hi = pts.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            if (pts[mid].T <= t) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }

    /// <summary>Values hold until the next point; yields each held span clipped to the view.</summary>
    private static IEnumerable<(HistoryPoint P, long T0, long T1)> Segments(IReadOnlyList<HistoryPoint> pts, long start, long end)
    {
        for (var i = Math.Max(0, LastAtOrBefore(pts, start)); i < pts.Count; i++)
        {
            if (pts[i].T >= end) yield break;
            var t0 = Math.Max(pts[i].T, start);
            var t1 = i + 1 < pts.Count ? Math.Min(pts[i + 1].T, end) : end;
            if (t1 > t0) yield return (pts[i], t0, t1);
        }
    }

    private static LaneStyle StyleFor(RenderContext c, LaneModel lane, HistoryPoint p)
    {
        var invalid = p.Validity == Validity.Invalid;
        var style = StyleResolver.Resolve(c.Layout.Styles, ValueFormatter.StyleKey(lane, p.Value), p.Confidence is < LowConfidence, invalid);
        return invalid && !c.Layout.Styles.ContainsKey(StyleResolver.InvalidStyleName) ? style with { Fill = "#888888" } : style;
    }

    private static Pen? BorderPen(LaneStyle style) => style.Border switch
    {
        null => Gfx.PenOf(Color.FromRgb(0x20, 0x20, 0x20), 1),
        "dashed" => new Pen(Brushes.White, 1.5) { DashStyle = DashStyles.Dash },
        _ => Gfx.PenOf(Colors.White, 1.5),
    };

    private static void DrawBoxes(RenderContext c, LaneModel lane, IReadOnlyList<HistoryPoint> pts, Rect r)
    {
        foreach (var (p, t0, t1) in Segments(pts, c.Map.Start, c.Map.End))
        {
            var x0 = c.Map.X(t0);
            var box = new Rect(x0, r.Top + 4, Math.Max(1, c.Map.X(t1) - x0), r.Height - 8);
            var style = StyleFor(c, lane, p);
            var fill = Gfx.ParseColor(style.Fill, Color.FromRgb(0x66, 0x66, 0x66));

            c.Dc.DrawRectangle(Gfx.Solid(fill), BorderPen(style), box);
            if (style.Hatch == true) c.Dc.DrawRectangle(Gfx.Hatch, null, box);

            if (box.Width < 24) continue;
            var text = p.Validity == Validity.Invalid ? "invalid" : ValueFormatter.Format(lane, p.Value);
            var ft = Gfx.Text(text, 12, Gfx.ContrastOn(fill), c.Ppd);
            if (ft.Width + 8 <= box.Width)
                c.Dc.DrawText(ft, new Point(box.Left + 4, box.Top + (box.Height - ft.Height) / 2));
        }
    }

    private static void DrawLines(RenderContext c, LaneModel lane, IReadOnlyList<HistoryPoint> pts, Rect r, bool singleValue)
    {
        var segments = Segments(pts, c.Map.Start, c.Map.End).ToList();
        var componentCount = singleValue ? 1 : Math.Max(1, lane.Components.Count);

        double? Pick(HistoryPoint p, int component)
        {
            if (p.Validity == Validity.Invalid) return null;
            return p.Value.Kind switch
            {
                ClientValueKind.Float => p.Value.AsFloat,
                ClientValueKind.Int => p.Value.AsInt,
                ClientValueKind.Vec => component < p.Value.AsVec.Length ? p.Value.AsVec[component] : null,
                _ => null,
            };
        }

        double min, max;
        if (lane.Spec.Range is { Count: 2 } range)
        {
            min = range[0];
            max = range[1];
        }
        else
        {
            var all = segments
                .SelectMany(s => Enumerable.Range(0, componentCount).Select(i => Pick(s.P, i)))
                .Where(v => v.HasValue)
                .Select(v => v!.Value)
                .ToList();
            min = all.Count > 0 ? all.Min() : 0;
            max = all.Count > 0 ? all.Max() : 1;
        }
        if (max - min < 1e-9) { min -= 1; max += 1; }

        var top = r.Top + 6;
        var height = r.Height - 12;
        double Y(double v) => top + height - (Math.Clamp(v, min, max) - min) / (max - min) * height;

        for (var comp = 0; comp < componentCount; comp++)
        {
            var color = singleValue ? DefaultLine : ComponentColors[comp % ComponentColors.Length];
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                var open = false;
                foreach (var (p, t0, t1) in segments)
                {
                    if (Pick(p, comp) is not { } v) { open = false; continue; }
                    var y = Y(v);
                    var x0 = c.Map.X(t0);
                    if (open) g.LineTo(new Point(x0, y), true, false);
                    else g.BeginFigure(new Point(x0, y), false, false);
                    g.LineTo(new Point(c.Map.X(t1), y), true, false);
                    open = true;
                }
            }
            geometry.Freeze();
            c.Dc.DrawGeometry(null, Gfx.PenOf(color, 1.5), geometry);
        }

        DrawInvalidSpans(c, segments, r);

        var dim = Gfx.Dim;
        c.Dc.DrawText(Gfx.Text(max.ToString("0.##"), 9, dim, c.Ppd), new Point(r.Left + 2, r.Top));
        c.Dc.DrawText(Gfx.Text(min.ToString("0.##"), 9, dim, c.Ppd), new Point(r.Left + 2, r.Bottom - 12));
    }

    private static void DrawInvalidSpans(RenderContext c, IEnumerable<(HistoryPoint P, long T0, long T1)> segments, Rect r)
    {
        foreach (var (p, t0, t1) in segments)
        {
            if (p.Validity != Validity.Invalid) continue;
            var x0 = c.Map.X(t0);
            c.Dc.DrawRectangle(Gfx.Solid(Color.FromArgb(0x90, InvalidGrey.R, InvalidGrey.G, InvalidGrey.B)), null,
                new Rect(x0, r.Top + 2, Math.Max(1, c.Map.X(t1) - x0), r.Height - 4));
        }
    }

    private static void DrawDigital(RenderContext c, LaneModel lane, IReadOnlyList<HistoryPoint> pts, Rect r)
    {
        var onColor = Color.FromRgb(0x3F, 0xA3, 0x4D);
        foreach (var (p, t0, t1) in Segments(pts, c.Map.Start, c.Map.End))
        {
            var x0 = c.Map.X(t0);
            var w = Math.Max(1, c.Map.X(t1) - x0);

            if (p.Validity == Validity.Invalid)
            {
                var s = StyleFor(c, lane, p);
                c.Dc.DrawRectangle(Gfx.Solid(Gfx.ParseColor(s.Fill, InvalidGrey)), null, new Rect(x0, r.Top + 6, w, r.Height - 12));
            }
            else if (p.Value.Kind == ClientValueKind.Bool && p.Value.AsBool)
            {
                var style = StyleFor(c, lane, p);
                var fill = style.Fill is null || style.Fill == LaneStyle.Neutral.Fill ? onColor : Gfx.ParseColor(style.Fill, onColor);
                c.Dc.DrawRectangle(Gfx.Solid(fill), null, new Rect(x0, r.Top + 6, w, r.Height - 12));
            }
            else
            {
                c.Dc.DrawRectangle(Gfx.Solid(Color.FromRgb(0x55, 0x55, 0x55)), null, new Rect(x0, r.Bottom - 8, w, 2));
            }
        }
    }

    private static void DrawSwatch(RenderContext c, LaneModel lane, IReadOnlyList<HistoryPoint> pts, Rect r)
    {
        foreach (var (p, t0, t1) in Segments(pts, c.Map.Start, c.Map.End))
        {
            var x0 = c.Map.X(t0);
            var box = new Rect(x0, r.Top + 4, Math.Max(1, c.Map.X(t1) - x0), r.Height - 8);

            Color fill;
            if (p.Validity == Validity.Invalid)
                fill = Gfx.ParseColor(StyleFor(c, lane, p).Fill, InvalidGrey);
            else if (p.Value.Kind == ClientValueKind.Vec && p.Value.AsVec.Length >= 3)
                fill = Color.FromRgb(Channel(p.Value.AsVec[0]), Channel(p.Value.AsVec[1]), Channel(p.Value.AsVec[2]));
            else
                fill = InvalidGrey;

            c.Dc.DrawRectangle(Gfx.Solid(fill), null, box);
            if (p.Confidence is < LowConfidence && c.Layout.Styles.TryGetValue(StyleResolver.LowConfidenceStyleName, out var low) && low.Hatch == true)
                c.Dc.DrawRectangle(Gfx.Hatch, null, box);
        }
    }

    private static byte Channel(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);
}
