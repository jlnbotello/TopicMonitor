using TopicMonitor.Contracts;

namespace TopicMonitor.Client;

/// <summary>Event data for <see cref="ClientSession.SeqGapDetected"/>.</summary>
public sealed class SeqGapEventArgs : EventArgs
{
    public required string Source { get; init; }
    public required ulong ExpectedSeq { get; init; }
    public required ulong ActualSeq { get; init; }
}

/// <summary>Event data for <see cref="ClientSession.CatalogVersionChanged"/>.</summary>
public sealed class CatalogVersionChangedEventArgs : EventArgs
{
    public required ulong OldVersion { get; init; }
    public required ulong NewVersion { get; init; }
}

/// <summary>
/// The network-free core of the client protocol: applies incoming
/// <c>SampleBatch</c>es to the state store, history and latency probe, detects per-source <c>seq</c> gaps,
/// and detects <c>catalog_version</c> changes.
/// <para>
/// This type never touches gRPC — <see cref="Ingest"/> takes a plain proto <c>SampleBatch</c>, so the
/// "client state store: deltas, gap detection, catalog change" unit tests can drive it
/// directly with synthetic messages. <see cref="PanelClient"/> is the thin network-facing wrapper that
/// feeds this type from a live stream and reacts to its events by re-<c>Describe()</c>ing and
/// re-<c>Subscribe</c>ing.
/// </para>
/// </summary>
public sealed class ClientSession
{
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, ulong> _lastSeqBySource = new();
    private ulong? _catalogVersion;
    private long? _lastReceivedT;

    public TopicStateStore State { get; } = new();
    public HistoryStore History { get; }
    public LatencyProbe Latency { get; } = new();

    /// <summary>Raised once per applied batch, after state/history/latency have all been updated. A WPF
    /// viewer marshals this onto its dispatcher before touching UI state (documented choice: a plain
    /// multicast <see cref="EventHandler{TEventArgs}"/> carrying the raw batch, "one
    /// change event per sample batch" — callers needing the full picture read it back from
    /// <see cref="State"/>/<see cref="History"/>).</summary>
    public event EventHandler<SampleBatch>? SampleApplied;

    /// <summary>Raised when a batch's <c>seq</c> is not the previous <c>seq + 1</c> for its <c>source</c>
    /// (snapshots are exempt — they establish a fresh baseline). <see cref="PanelClient"/> treats this as a
    /// signal to resync: re-<c>Describe()</c> + re-<c>Subscribe</c> from the last good <c>t</c>.</summary>
    public event EventHandler<SeqGapEventArgs>? SeqGapDetected;

    /// <summary>Raised when a batch's <c>catalog_version</c> differs from the previously seen version (plan
    /// section 6: "a scenario reload that changes topics bumps catalog_version; clients call Describe()
    /// again"). Never raised for the very first batch, which only establishes the baseline version.</summary>
    public event EventHandler<CatalogVersionChangedEventArgs>? CatalogVersionChanged;

    public ClientSession(TimeProvider? timeProvider = null, int historyCapacityPerTopic = 5000)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        History = new HistoryStore(historyCapacityPerTopic);
    }

    /// <summary>The last successfully applied batch's <c>t</c> — the resume point for <c>from_time</c> on
    /// reconnect ("Reconnect: resumes with from_time = last received t").</summary>
    public long? LastReceivedT => _lastReceivedT;

    /// <summary>Applies one received batch: detects catalog/seq anomalies, updates state/history, records a
    /// latency sample, then raises <see cref="SampleApplied"/>. Safe to call repeatedly with synthetic
    /// batches from a unit test; no I/O happens here.</summary>
    public void Ingest(SampleBatch batch)
    {
        var recv = _timeProvider.GetTimestamp();

        CheckCatalogVersion(batch);
        CheckSeqGap(batch);

        State.Apply(batch);
        History.Apply(batch);
        Latency.Record(recv, batch.TProcessed);

        if (_lastReceivedT is null || batch.T > _lastReceivedT) _lastReceivedT = batch.T;

        SampleApplied?.Invoke(this, batch);
    }

    /// <summary>Resets per-source <c>seq</c> tracking. Call right after starting a fresh <c>Subscribe</c>
    /// call (initial connect or a reconnect) so the first post-resync batch for a source is never mistaken
    /// for a gap, regardless of whether the server resets its sequence counter on resubscribe.</summary>
    public void ResetSeqTracking() => _lastSeqBySource.Clear();

    private void CheckCatalogVersion(SampleBatch batch)
    {
        if (_catalogVersion is { } current && current != batch.CatalogVersion)
        {
            CatalogVersionChanged?.Invoke(this, new CatalogVersionChangedEventArgs
            {
                OldVersion = current,
                NewVersion = batch.CatalogVersion,
            });
        }

        _catalogVersion = batch.CatalogVersion;
    }

    private void CheckSeqGap(SampleBatch batch)
    {
        var source = batch.Source;
        if (!batch.IsSnapshot && _lastSeqBySource.TryGetValue(source, out var last))
        {
            var expected = last + 1;
            if (batch.Seq != expected)
            {
                SeqGapDetected?.Invoke(this, new SeqGapEventArgs
                {
                    Source = source,
                    ExpectedSeq = expected,
                    ActualSeq = batch.Seq,
                });
            }
        }

        _lastSeqBySource[source] = batch.Seq;
    }
}
