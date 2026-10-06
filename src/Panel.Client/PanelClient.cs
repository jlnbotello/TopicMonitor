using Grpc.Core;
using Grpc.Net.Client;
using Panel.Contracts;

namespace Panel.Client;

/// <summary>
/// Top-level entry point for <c>Panel.Client</c> (plan section 7). Wraps a <see cref="GrpcChannel"/> +
/// <c>TopicService.TopicServiceClient</c>, fetches and caches the catalog, and runs a background read loop
/// that feeds a <see cref="ClientSession"/> (state store, history, latency probe) from the server's
/// <c>Subscribe</c> stream — reconnecting on transport failure and resyncing on a detected seq gap or
/// catalog change, so neither the WPF viewer nor a test has to touch gRPC streaming types directly.
/// </summary>
public sealed class PanelClient : IAsyncDisposable
{
    private readonly GrpcChannel _channel;
    private readonly TopicService.TopicServiceClient _grpc;
    private readonly bool _ownsChannel;
    private readonly TimeProvider _timeProvider;

    private CancellationTokenSource? _subscriptionCts;
    private Task? _readLoop;
    private IReadOnlyList<string> _patterns = Array.Empty<string>();
    private DeliveryMode _mode = DeliveryMode.Lossless;
    private volatile bool _resyncRequested;

    /// <summary>The network-free protocol engine; exposed for callers that want direct access to its
    /// events/state rather than going through the convenience members below.</summary>
    public ClientSession Session { get; }

    public TopicStateStore State => Session.State;
    public HistoryStore History => Session.History;
    public LatencyProbe Latency => Session.Latency;

    /// <summary>The most recently fetched catalog, or <c>null</c> before the first <see cref="DescribeAsync"/>.</summary>
    public TopicCatalog? Catalog { get; private set; }

    /// <summary>Raised once per applied batch; forwards <see cref="ClientSession.SampleApplied"/> so a
    /// viewer only has to subscribe to one type (see that event's doc comment for the event-shape choice).</summary>
    public event EventHandler<SampleBatch>? SampleReceived;

    /// <summary>Raised whenever the cached catalog is refreshed, whether from an explicit
    /// <see cref="DescribeAsync"/> call or an automatic one triggered by a <c>catalog_version</c> bump.</summary>
    public event EventHandler<TopicCatalog>? CatalogChanged;

    private PanelClient(GrpcChannel channel, bool ownsChannel, TimeProvider? timeProvider, int historyCapacityPerTopic)
    {
        _channel = channel;
        _ownsChannel = ownsChannel;
        _grpc = new TopicService.TopicServiceClient(channel);
        _timeProvider = timeProvider ?? TimeProvider.System;

        Session = new ClientSession(_timeProvider, historyCapacityPerTopic);
        Session.SampleApplied += (_, batch) => SampleReceived?.Invoke(this, batch);
        Session.CatalogVersionChanged += (_, _) => _ = TryRefreshCatalogAsync();
        Session.SeqGapDetected += (_, _) => _resyncRequested = true;
    }

    /// <summary>Connects to <c>address</c> (e.g. <c>http://127.0.0.1:5001</c>) and owns the resulting
    /// channel: <see cref="DisposeAsync"/> disposes it.</summary>
    public static PanelClient ConnectTo(string address, TimeProvider? timeProvider = null, int historyCapacityPerTopic = 5000)
    {
        var channel = GrpcChannel.ForAddress(address);
        return new PanelClient(channel, ownsChannel: true, timeProvider, historyCapacityPerTopic);
    }

    /// <summary>Wraps an existing <see cref="GrpcChannel"/> (e.g. one a test harness built in-process) that
    /// the caller keeps owning: <see cref="DisposeAsync"/> leaves it open.</summary>
    public static PanelClient ForChannel(GrpcChannel channel, TimeProvider? timeProvider = null, int historyCapacityPerTopic = 5000) =>
        new(channel, ownsChannel: false, timeProvider, historyCapacityPerTopic);

    /// <summary>Fetches the topic catalog, caches it as <see cref="Catalog"/>, and raises
    /// <see cref="CatalogChanged"/>.</summary>
    public async Task<TopicCatalog> DescribeAsync(CancellationToken ct = default)
    {
        var catalog = await _grpc.DescribeAsync(new DescribeRequest(), cancellationToken: ct).ConfigureAwait(false);
        Catalog = catalog;
        CatalogChanged?.Invoke(this, catalog);
        return catalog;
    }

    /// <summary>Looks up a topic's id by name in the cached <see cref="Catalog"/>, or <c>null</c> if no
    /// catalog has been fetched yet or no topic has that name.</summary>
    public uint? ResolveTopicId(string topicName)
    {
        var info = Catalog?.Topics.FirstOrDefault(t => t.Name == topicName);
        return info is null ? null : info.Id;
    }

    /// <summary>Current state of the topic named <paramref name="topicName"/>, resolved via
    /// <see cref="Catalog"/>, or <c>null</c> if the topic is unknown or has no state yet.</summary>
    public TopicState? GetState(string topicName) =>
        ResolveTopicId(topicName) is { } id ? State.Get(id) : null;

    /// <summary>History of the topic named <paramref name="topicName"/>, resolved via
    /// <see cref="Catalog"/>; empty if the topic is unknown or has no history yet.</summary>
    public IReadOnlyList<HistoryPoint> GetHistory(string topicName) =>
        ResolveTopicId(topicName) is { } id ? History.Get(id) : Array.Empty<HistoryPoint>();

    public async Task<TimeReply> GetTimeAsync(CancellationToken ct = default) =>
        await _grpc.GetTimeAsync(new TimeRequest(), cancellationToken: ct).ConfigureAwait(false);

    /// <summary>Single-sample round-trip offset estimate against the server's mono clock (see
    /// <see cref="TimeSync"/>). Not needed on the same machine; wired up per plan section 7 for a future
    /// remote client.</summary>
    public async Task<TimeOffsetEstimate> EstimateTimeOffsetAsync(CancellationToken ct = default)
    {
        var send = _timeProvider.GetTimestamp();
        var reply = await GetTimeAsync(ct).ConfigureAwait(false);
        var recv = _timeProvider.GetTimestamp();
        return TimeSync.Estimate(send, recv, reply);
    }

    /// <summary>
    /// Starts (or restarts) a subscription: fetches the catalog if needed, then begins a background read
    /// loop that feeds <see cref="Session"/> and transparently reconnects — on stream failure (plan section
    /// 6's "a Lossless subscriber behind the buffer is disconnected with a resync code") or on a detected
    /// seq gap — resuming with <c>from_time</c> = the last successfully received sample's <c>t</c> (plan
    /// section 7).
    /// </summary>
    public async Task SubscribeAsync(IReadOnlyList<string> patterns, DeliveryMode mode, long? fromTime = null, CancellationToken ct = default)
    {
        if (Catalog is null) await DescribeAsync(ct).ConfigureAwait(false);

        _patterns = patterns;
        _mode = mode;

        await StopSubscriptionAsync().ConfigureAwait(false);

        _subscriptionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _readLoop = RunReadLoopAsync(fromTime, _subscriptionCts.Token);
    }

    public async Task StopSubscriptionAsync()
    {
        if (_subscriptionCts is null) return;

        var cts = _subscriptionCts;
        var loop = _readLoop;
        _subscriptionCts = null;
        _readLoop = null;

        cts.Cancel();
        try
        {
            if (loop is not null) await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected: we just cancelled it.
        }
        finally
        {
            cts.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopSubscriptionAsync().ConfigureAwait(false);
        if (_ownsChannel) _channel.Dispose();
    }

    private async Task RunReadLoopAsync(long? fromTime, CancellationToken ct)
    {
        var nextFromTime = fromTime;

        while (!ct.IsCancellationRequested)
        {
            Session.ResetSeqTracking();

            var request = new SubscribeRequest { Mode = _mode };
            request.Patterns.AddRange(_patterns);
            if (nextFromTime.HasValue) request.FromTime = nextFromTime.Value;

            using var call = _grpc.Subscribe(request, cancellationToken: ct);

            try
            {
                await foreach (var batch in call.ResponseStream.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    Session.Ingest(batch);

                    if (_resyncRequested)
                    {
                        _resyncRequested = false;
                        break; // fall through: resubscribe below, from the latest good t.
                    }
                }
                // Server closed the stream cleanly; still worth a reconnect attempt below.
            }
            catch (RpcException) when (!ct.IsCancellationRequested)
            {
                // Transport fault, or a Lossless subscriber that fell behind (Aborted/DataLoss per plan
                // section 5). Fall through to resync.
            }

            if (ct.IsCancellationRequested) break;

            nextFromTime = Session.LastReceivedT ?? nextFromTime;

            // The catalog may have changed while we were disconnected (plan section 6); refresh it before
            // resubscribing so callers see current topics as soon as the stream resumes. Best effort: if
            // this fails we still retry the subscribe itself below.
            try { await DescribeAsync(ct).ConfigureAwait(false); }
            catch (RpcException) { }

            try { await Task.Delay(TimeSpan.FromMilliseconds(200), _timeProvider, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TryRefreshCatalogAsync()
    {
        try { await DescribeAsync().ConfigureAwait(false); }
        catch
        {
            // Best effort: the next successful Describe (e.g. after a reconnect) will pick this up.
        }
    }
}
