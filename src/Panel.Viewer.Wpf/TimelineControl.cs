using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Panel.Client;
using Panel.Contracts;
using Panel.Viewer.Layout;

namespace Panel.Viewer.Wpf;

/// <summary>One shared time axis with one lane per topic; a single <see cref="DrawingVisual"/> renders every lane type.</summary>
public sealed class TimelineControl : FrameworkElement
{
    private const double LabelWidth = 190;
    private const double RulerHeight = 26;
    private const double LaneHeight = 40;
    private const double RightMargin = 8;
    private static readonly double[] TickSteps = { 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 30, 60, 300 };

    private readonly DrawingVisual _visual = new();
    private IReadOnlyList<LaneModel> _lanes = Array.Empty<LaneModel>();
    private LayoutModel _layoutModel = LayoutModel.Empty;
    private PanelClient? _source;

    private long _frequency = Stopwatch.Frequency;
    private long _clientToServer;
    private long _origin = Stopwatch.GetTimestamp();
    private TimeSpan _window = TimeSpan.FromSeconds(10);
    private bool _paused;
    private long _pausedEnd;

    private Point? _mouse;
    private Point? _dragStart;
    private long _dragStartEnd;
    private bool _dragged;
    private long? _cursorA, _cursorB;
    private (string LaneKey, HistoryPoint Point)? _pinned;
    private bool _dirty = true;

    public event Action<LaneModel, RendererKind>? RendererChangeRequested;

    public TimelineControl()
    {
        AddVisualChild(_visual);
        ClipToBounds = true;
        Loaded += (_, _) => CompositionTarget.Rendering += OnFrame;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnFrame;
    }

    public PanelClient? DataSource
    {
        get => _source;
        set { _source = value; _dirty = true; }
    }

    public TimeSpan Window
    {
        get => _window;
        set
        {
            _window = TimeSpan.FromSeconds(Math.Clamp(value.TotalSeconds, 0.2, 3600));
            _dirty = true;
        }
    }

    public bool Paused
    {
        get => _paused;
        set
        {
            if (value == _paused) return;
            _paused = value;
            if (value) _pausedEnd = LiveNow;
            _dirty = true;
        }
    }

    private long LiveNow => Stopwatch.GetTimestamp() + _clientToServer;

    private long ViewEnd => _paused ? _pausedEnd : LiveNow;

    private long Span => Math.Max(1, (long)(_window.TotalSeconds * _frequency));

    /// <summary>Maps the client clock to the server's tick domain; call once the server's time is known.</summary>
    public void SetTimeBase(long serverFrequency, long clientToServerOffset)
    {
        _frequency = serverFrequency;
        _clientToServer = clientToServerOffset;
        _origin = LiveNow;
        _dirty = true;
    }

    public void SetLanes(LayoutModel layout, IReadOnlyList<LaneModel> lanes)
    {
        _layoutModel = layout;
        _lanes = lanes;
        _pinned = null;
        InvalidateMeasure();
        _dirty = true;
    }

    public void ZoomBy(double factor, double? anchorX = null)
    {
        var newWindow = TimeSpan.FromSeconds(Math.Clamp(_window.TotalSeconds * factor, 0.2, 3600));
        var actual = newWindow.TotalSeconds / _window.TotalSeconds;

        if (_paused && anchorX is { } x)
        {
            var anchorT = PlotMap().T(x);
            _pausedEnd = Math.Min(LiveNow, anchorT + (long)((_pausedEnd - anchorT) * actual));
        }

        Window = newWindow;
    }

    public void ClearCursors()
    {
        _cursorA = _cursorB = null;
        _pinned = null;
        _dirty = true;
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => _visual;

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 900 : availableSize.Width,
            RulerHeight + Math.Max(1, _lanes.Count) * LaneHeight);

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _dirty = true;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (_paused && !_dirty) return;
        _dirty = false;
        Render();
    }

    private double Sec(long t) => (t - _origin) / (double)_frequency;

    private TimeMap PlotMap()
    {
        var end = ViewEnd;
        return new TimeMap(end - Span, end, LabelWidth, Math.Max(10, ActualWidth - LabelWidth - RightMargin));
    }

    private Rect LaneRect(int index, TimeMap map) => new(map.Left, RulerHeight + index * LaneHeight, map.Width, LaneHeight);

    private IReadOnlyList<HistoryPoint> History(LaneModel lane) =>
        lane.Topic is null || _source is null ? Array.Empty<HistoryPoint>() : _source.History.Get(lane.Topic.Id);

    private void Render()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        var ppd = VisualTreeHelper.GetDpi(_visual).PixelsPerDip;
        var map = PlotMap();

        using var dc = _visual.RenderOpen();
        var ctx = new RenderContext(dc, map, _layoutModel, ppd);

        dc.DrawRectangle(Gfx.Background, null, new Rect(0, 0, ActualWidth, ActualHeight));

        var tickStep = ChooseTickStep(map);
        DrawGrid(dc, map, tickStep, ppd);

        for (var i = 0; i < _lanes.Count; i++)
        {
            var lane = _lanes[i];
            var rect = LaneRect(i, map);

            if (i % 2 == 1) dc.DrawRectangle(Gfx.RowAlt, null, new Rect(0, rect.Top, ActualWidth, rect.Height));
            dc.DrawText(Gfx.Text(lane.DisplayName, 12, Gfx.Foreground, ppd, LabelWidth - 12), new Point(6, rect.Top + 4));
            dc.DrawText(Gfx.Text(lane.Spec.GroupName, 10, Gfx.Dim, ppd, LabelWidth - 12), new Point(6, rect.Top + 22));

            dc.PushClip(new RectangleGeometry(rect));
            if (lane.Topic is null)
                dc.DrawText(Gfx.Text("not on server", 11, Gfx.Dim, ppd), new Point(rect.Left + 8, rect.Top + 12));
            else
                LaneRenderers.Draw(ctx, lane, History(lane), rect);
            dc.Pop();

            dc.DrawLine(Gfx.GridPen, new Point(0, rect.Bottom), new Point(ActualWidth, rect.Bottom));
        }

        DrawCursors(dc, map, ppd);
        DrawHoverAndPin(dc, map, ppd);
    }

    private double ChooseTickStep(TimeMap map)
    {
        var secondsPerPixel = _window.TotalSeconds / map.Width;
        foreach (var step in TickSteps)
            if (step / secondsPerPixel >= 70) return step;
        return TickSteps[^1];
    }

    private void DrawGrid(DrawingContext dc, TimeMap map, double step, double ppd)
    {
        var startSec = Sec(map.Start);
        var endSec = Sec(map.End);
        var bottom = RulerHeight + Math.Max(1, _lanes.Count) * LaneHeight;

        dc.DrawLine(Gfx.SeparatorPen, new Point(0, RulerHeight), new Point(ActualWidth, RulerHeight));
        dc.DrawLine(Gfx.SeparatorPen, new Point(LabelWidth, 0), new Point(LabelWidth, bottom));

        for (var s = Math.Ceiling(startSec / step) * step; s <= endSec; s += step)
        {
            var x = map.X(_origin + (long)(s * _frequency));
            if (x < LabelWidth) continue;
            dc.DrawLine(Gfx.GridPen, new Point(x, RulerHeight), new Point(x, bottom));
            dc.DrawText(Gfx.Text(s.ToString("0.##", CultureInfo.InvariantCulture) + " s", 10, Gfx.Dim, ppd), new Point(x + 3, 6));
        }
    }

    private void DrawCursors(DrawingContext dc, TimeMap map, double ppd)
    {
        var bottom = RulerHeight + Math.Max(1, _lanes.Count) * LaneHeight;

        void One(long? t, string name, Color color)
        {
            if (t is not { } tt) return;
            var x = map.X(tt);
            if (x < LabelWidth || x > LabelWidth + map.Width) return;
            var pen = new Pen(Gfx.Solid(color), 1.5) { DashStyle = DashStyles.Dash };
            dc.DrawLine(pen, new Point(x, RulerHeight), new Point(x, bottom));
            dc.DrawText(Gfx.Text($"{name} {Sec(tt):0.000}s", 10, Gfx.Solid(color), ppd), new Point(x + 3, RulerHeight + 2));
        }

        One(_cursorA, "A", Colors.Gold);
        One(_cursorB, "B", Colors.Cyan);

        if (_cursorA is { } a && _cursorB is { } b)
        {
            var dtMs = Math.Abs(b - a) * 1000.0 / _frequency;
            dc.DrawText(Gfx.Text($"\u0394t = {dtMs:0.0} ms", 11, Gfx.Solid(Colors.White), ppd), new Point(8, RulerHeight - 20));
        }
    }

    private void DrawHoverAndPin(DrawingContext dc, TimeMap map, double ppd)
    {
        if (_pinned is { } pin)
        {
            var index = _lanes.ToList().FindIndex(l => l.Key == pin.LaneKey);
            var x = map.X(pin.Point.T);
            if (index >= 0 && x >= LabelWidth && x <= LabelWidth + map.Width)
            {
                var rect = LaneRect(index, map);
                dc.DrawLine(Gfx.PenOf(Colors.Orange, 1.5), new Point(x, rect.Top), new Point(x, rect.Bottom));
                DrawInfoBox(dc, InfoText(_lanes[index], pin.Point, full: true), new Point(x, rect.Bottom), 380, ppd);
            }
        }

        if (_mouse is not { } m || _dragStart is not null || m.X < LabelWidth || m.X > LabelWidth + map.Width) return;

        var hoverLane = (int)((m.Y - RulerHeight) / LaneHeight);
        if (m.Y < RulerHeight || hoverLane < 0 || hoverLane >= _lanes.Count) return;

        dc.DrawLine(Gfx.PenOf(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF), 1), new Point(m.X, RulerHeight), new Point(m.X, ActualHeight));

        var lane = _lanes[hoverLane];
        if (lane.Topic is not null && LaneRenderers.FindAt(History(lane), map.T(m.X)) is { } p)
            DrawInfoBox(dc, InfoText(lane, p, full: false), m, 320, ppd);
    }

    private string InfoText(LaneModel lane, HistoryPoint p, bool full)
    {
        var value = ValueFormatter.Format(lane, p.Value);
        if (!full && value.Length > 60) value = value[..60] + "\u2026";

        var sb = new StringBuilder();
        sb.AppendLine(lane.Spec.Topic);
        sb.AppendLine($"value: {value}");
        if (p.Validity == Validity.Invalid) sb.AppendLine("validity: invalid");
        sb.AppendLine($"t: {Sec(p.T):0.000} s");
        if (p.Confidence is { } conf) sb.AppendLine($"confidence: {conf:0.00}");
        if (p.EvidenceSince is { } ev) sb.AppendLine($"evidence since: {Sec(ev):0.000} s");
        return sb.ToString().TrimEnd();
    }

    private void DrawInfoBox(DrawingContext dc, string text, Point anchor, double maxWidth, double ppd)
    {
        var ft = Gfx.Text(text, 12, Brushes.White, ppd, maxWidth);
        var w = ft.Width + 14;
        var h = ft.Height + 10;
        var x = Math.Clamp(anchor.X + 12, 0, Math.Max(0, ActualWidth - w));
        var y = Math.Clamp(anchor.Y + 12, 0, Math.Max(0, ActualHeight - h));

        dc.DrawRoundedRectangle(Gfx.Solid(Color.FromArgb(0xEE, 0x20, 0x20, 0x20)), Gfx.PenOf(Colors.Gray, 1), new Rect(x, y, w, h), 3, 3);
        dc.DrawText(ft, new Point(x + 7, y + 5));
    }

    private (int Lane, TimeMap Map)? LaneAt(Point p)
    {
        var map = PlotMap();
        var index = (int)((p.Y - RulerHeight) / LaneHeight);
        return p.Y >= RulerHeight && index >= 0 && index < _lanes.Count ? (index, map) : null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mouse = e.GetPosition(this);
        _dirty = true;

        if (_dragStart is not { } start) return;

        var dx = _mouse.Value.X - start.X;
        if (!_dragged && Math.Abs(dx) < 4) return;

        _dragged = true;
        if (!_paused) { _paused = true; _pausedEnd = LiveNow; _dragStartEnd = _pausedEnd; }
        var ticksPerPixel = (double)Span / PlotMap().Width;
        _pausedEnd = Math.Min(LiveNow, _dragStartEnd - (long)(dx * ticksPerPixel));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _mouse = null;
        _dirty = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _dragStart = e.GetPosition(this);
        _dragStartEnd = ViewEnd;
        _dragged = false;
        CaptureMouse();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        ReleaseMouseCapture();

        var wasClick = _dragStart is not null && !_dragged;
        var pos = e.GetPosition(this);
        _dragStart = null;
        _dirty = true;
        if (!wasClick || pos.X < LabelWidth) return;

        var map = PlotMap();
        var t = map.T(pos.X);

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (_cursorA is null) _cursorA = t;
            else if (_cursorB is null) _cursorB = t;
            else { _cursorA = t; _cursorB = null; }
            return;
        }

        if (LaneAt(pos) is not { } hit) { _pinned = null; return; }

        var lane = _lanes[hit.Lane];
        var point = lane.Topic is null ? null : LaneRenderers.FindAt(History(lane), t);
        _pinned = point is null || _pinned?.Point == point ? null : (lane.Key, point);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        ZoomBy(e.Delta > 0 ? 0.8 : 1.25, e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);

        var menu = new ContextMenu { PlacementTarget = this };

        if (LaneAt(e.GetPosition(this)) is { } hit)
        {
            var lane = _lanes[hit.Lane];
            menu.Items.Add(new MenuItem { Header = lane.Spec.Topic, IsEnabled = false });

            var renderers = new MenuItem { Header = "Renderer" };
            foreach (var kind in LaneModelBuilder.Applicable(lane))
            {
                var item = new MenuItem { Header = kind.ToYamlString(), IsCheckable = true, IsChecked = kind == lane.Renderer };
                var chosen = kind;
                item.Click += (_, _) => RendererChangeRequested?.Invoke(lane, chosen);
                renderers.Items.Add(item);
            }
            renderers.IsEnabled = renderers.Items.Count > 0;
            menu.Items.Add(renderers);
            menu.Items.Add(new Separator());
        }

        var clear = new MenuItem { Header = "Clear cursors and pin" };
        clear.Click += (_, _) => ClearCursors();
        menu.Items.Add(clear);

        menu.IsOpen = true;
        e.Handled = true;
    }
}
