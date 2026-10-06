using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Panel.Client;
using Panel.Contracts;
using Panel.Viewer.Layout;

namespace Panel.Viewer.Wpf;

public partial class MainWindow : Window
{
    private static readonly TimeSpan HistoryReplay = TimeSpan.FromMinutes(5);

    private readonly ViewerOptions _options;
    private readonly PanelClient _client;
    private readonly LayoutFileService _layoutFile;
    private readonly Dictionary<string, RendererKind> _rendererOverrides = new();
    private IReadOnlyList<LaneModel> _lanes = Array.Empty<LaneModel>();
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private ulong _catalogVersion;
    private string? _connectError;
    private long _lastRecv;
    private long _batchCount;
    private long _rateCountBase;
    private long _rateTimeBase = Stopwatch.GetTimestamp();
    private double _rate;

    public MainWindow(ViewerOptions options)
    {
        InitializeComponent();
        _options = options;

        _client = PanelClient.ConnectTo(options.ServerAddress);
        TimelineView.DataSource = _client;
        TimelineView.RendererChangeRequested += OnRendererChangeRequested;
        TimelineView.RendererScopeNote = DescribeRendererScope;
        TimelineView.CanSaveUnassigned = CanSaveUnassigned;
        TimelineView.SaveUnassignedRequested += OnSaveUnassignedRequested;

        _client.SampleReceived += (_, _) =>
        {
            Interlocked.Increment(ref _batchCount);
            Interlocked.Exchange(ref _lastRecv, Stopwatch.GetTimestamp());
        };
        _client.CatalogChanged += (_, catalog) => Dispatcher.BeginInvoke(() => OnCatalogChanged(catalog));

        _layoutFile = new LayoutFileService(options.LayoutPath);
        _layoutFile.Reloaded += () => Dispatcher.BeginInvoke(OnLayoutReloaded);

        _statusTimer.Tick += (_, _) => UpdateStatus();

        Loaded += async (_, _) =>
        {
            _layoutFile.Start();
            RebuildLanes(applyWindow: true);
            _statusTimer.Start();
            await ConnectAsync();
        };
        Closed += (_, _) =>
        {
            _cts.Cancel();
            _statusTimer.Stop();
            _layoutFile.Dispose();
            _ = _client.DisposeAsync();
        };
    }

    // The client retries on its own once subscribed; only the first connect needs a retry loop here.
    private async Task ConnectAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var offset = await _client.EstimateTimeOffsetAsync(ct);
                TimelineView.SetTimeBase(offset.ServerMonoFrequency, offset.OffsetTicks);

                var serverNow = Stopwatch.GetTimestamp() + offset.OffsetTicks;
                var from = serverNow - (long)(HistoryReplay.TotalSeconds * offset.ServerMonoFrequency);
                await _client.SubscribeAsync(new[] { "*" }, DeliveryMode.Lossless, from, ct);

                _connectError = null;
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _connectError = ex.Message;
                try { await Task.Delay(1000, ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private void OnCatalogChanged(TopicCatalog catalog)
    {
        // Topic ids may be reassigned when a reload changes the topic set, so old history no longer applies.
        if (_catalogVersion != 0 && _catalogVersion != catalog.CatalogVersion)
            _client.History.Clear();

        _catalogVersion = catalog.CatalogVersion;
        RebuildLanes(applyWindow: false);
    }

    private void OnLayoutReloaded() => RebuildLanes(applyWindow: true);

    private void RebuildLanes(bool applyWindow)
    {
        var catalog = _client.Catalog;
        var baseModel = _layoutFile.Document?.Model ?? LayoutModel.Empty;
        var messages = new List<string>();
        if (_layoutFile.LastError is { } error) messages.Add(error);

        LayoutModel effective;
        IReadOnlyList<ExpandedLane> lanes;
        try
        {
            effective = catalog is null
                ? baseModel
                : LayoutDefaults.FillMissingLanes(baseModel, catalog.Topics.Select(t => new CatalogTopic(t.Name, LaneModelBuilder.ToLaneType(t.Type))));
            lanes = TemplateExpander.Expand(effective);

            if (catalog is not null)
            {
                var check = LayoutCatalogCheck.Check(baseModel, catalog.Topics.Select(t => t.Name));
                if (check.MissingInCatalog.Count > 0)
                    messages.Add("Layout topics not on the server: " + string.Join(", ", check.MissingInCatalog));
                if (check.UnusedInLayout.Count > 0)
                    messages.Add("Server topics without a lane (added under Unassigned): " + string.Join(", ", check.UnusedInLayout));
            }
        }
        catch (LayoutModelException ex)
        {
            messages.Add(ex.Message);
            baseModel = LayoutModel.Empty;
            effective = catalog is null
                ? baseModel
                : LayoutDefaults.FillMissingLanes(baseModel, catalog.Topics.Select(t => new CatalogTopic(t.Name, LaneModelBuilder.ToLaneType(t.Type))));
            lanes = TemplateExpander.Expand(effective);
        }

        TimelineView.SetLanes(effective, _lanes = LaneModelBuilder.Build(lanes, catalog, _rendererOverrides));
        if (applyWindow) TimelineView.Window = effective.Window;

        LayoutBanner.Visibility = messages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        LayoutBannerText.Text = string.Join(Environment.NewLine, messages);
    }

    private string? DescribeRendererScope(LaneModel lane)
    {
        var model = _layoutFile.Document?.Model;
        if (model is null || LayoutCatalogCheck.FindLaneIndex(model, lane.Spec) is null)
            return "Not saved to the file (this session only)";

        var template = model.Groups.FirstOrDefault(g => g.Name == lane.Spec.GroupName)?.Use;
        if (template is null) return "Saved to layout.yaml";

        var groups = model.Groups.Count(g => g.Use == template);
        return $"Saved to template '{template}' (affects {groups} group{(groups == 1 ? "" : "s")})";
    }

    private bool CanSaveUnassigned() => UnassignedTopics().Count > 0;

    private IReadOnlyList<string> UnassignedTopics()
    {
        var document = _layoutFile.Document;
        var catalog = _client.Catalog;
        return document is null || catalog is null
            ? Array.Empty<string>()
            : LayoutCatalogCheck.Check(document.Model, catalog.Topics.Select(t => t.Name)).UnusedInLayout;
    }

    private void OnSaveUnassignedRequested()
    {
        var topics = UnassignedTopics().ToHashSet();
        var specs = _lanes
            .Where(l => topics.Contains(l.Spec.Topic))
            .Select(l => new LaneSpec(l.Spec.Topic, As: l.Renderer.ToYamlString(), Unit: l.Spec.Unit, Space: l.Spec.Space))
            .ToList();

        try
        {
            _layoutFile.AppendGroup(LayoutDefaults.FillGroupName, specs);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "Could not save layout", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        foreach (var lane in _lanes.Where(l => topics.Contains(l.Spec.Topic)))
            _rendererOverrides.Remove(lane.Key);
        RebuildLanes(applyWindow: false);
    }

    private void OnRendererChangeRequested(LaneModel lane, RendererKind renderer)
    {
        var document = _layoutFile.Document;
        var laneIndex = document is null ? null : LayoutCatalogCheck.FindLaneIndex(document.Model, lane.Spec);

        if (laneIndex is not { } index)
        {
            // Auto-generated lanes are not in the file; keep the choice for this session only.
            _rendererOverrides[lane.Key] = renderer;
        }
        else
        {
            try
            {
                _layoutFile.SetRenderer(index, renderer);
                _rendererOverrides.Remove(lane.Key);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                MessageBox.Show(this, ex.Message, "Could not save layout", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        RebuildLanes(applyWindow: false);
    }

    private void UpdateStatus()
    {
        var now = Stopwatch.GetTimestamp();
        var frequency = (double)Stopwatch.Frequency;

        var lastRecv = Interlocked.Read(ref _lastRecv);
        ConnectionText.Text = lastRecv == 0
            ? $"Connecting to {_options.ServerAddress}" + (_connectError is null ? string.Empty : $" ({_connectError})")
            : (now - lastRecv) / frequency < 2
                ? $"Connected: {_options.ServerAddress}"
                : $"No data for {(now - lastRecv) / frequency:0} s";

        ScenarioText.Text = _catalogVersion == 0 ? "Scenario: -" : $"Scenario version: {_catalogVersion}";

        var elapsed = (now - _rateTimeBase) / frequency;
        if (elapsed >= 1)
        {
            var count = Interlocked.Read(ref _batchCount);
            _rate = (count - _rateCountBase) / elapsed;
            _rateCountBase = count;
            _rateTimeBase = now;
        }
        RateText.Text = $"Rate: {_rate:0.0} batches/s";

        LatencyText.Text = _client.Latency.Summary(TimeSpan.FromSeconds(10), Stopwatch.Frequency) is var (p50, max)
            ? $"Latency (10 s): p50 {p50 * 1000 / frequency:0.0} ms, max {max * 1000 / frequency:0.0} ms"
            : "Latency: -";

        WindowText.Text = $"Window: {TimelineView.Window.TotalSeconds:0.#} s";
        PauseButton.IsChecked = TimelineView.Paused;
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e) => TimelineView.Paused = PauseButton.IsChecked == true;

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => TimelineView.ZoomBy(0.8);

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => TimelineView.ZoomBy(1.25);

    private void ClearCursors_Click(object sender, RoutedEventArgs e) => TimelineView.ClearCursors();
}