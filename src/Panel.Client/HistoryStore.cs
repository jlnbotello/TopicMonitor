using Panel.Contracts;

namespace Panel.Client;

/// <summary>One historical observation of a topic, as kept by <see cref="HistoryStore"/>.</summary>
public sealed record HistoryPoint(
    long T,
    ClientValue Value,
    float? Confidence,
    Validity Validity,
    long? EvidenceSince);

/// <summary>
/// Per-topic time series so a UI can show "the last N seconds" (plan section 8's lane window) without
/// re-querying the server (plan section 7: "History: per-topic time series for the visible window and
/// beyond").
/// <para>
/// Retention policy: a bounded count per topic (default 5000 points) rather than a wall-clock window. A
/// count avoids needing to know the server's QPC tick frequency just to retain history, and keeps this type
/// trivial to unit test with synthetic batches. 5000 points covers several minutes of history even at a
/// busy 20-50 Hz scenario rate while bounding memory for long-running sessions; a caller that wants a
/// strict time window (e.g. the layout's <c>window: 10s</c>) filters <see cref="Get"/>'s result by
/// <c>T</c> itself once it knows the server's tick frequency (<see cref="TimeReply.MonoFrequency"/>).
/// </para>
/// </summary>
public sealed class HistoryStore
{
    private readonly int _maxPointsPerTopic;
    private readonly Dictionary<uint, Queue<HistoryPoint>> _series = new();
    private readonly object _gate = new();

    public HistoryStore(int maxPointsPerTopic = 5000)
    {
        if (maxPointsPerTopic <= 0) throw new ArgumentOutOfRangeException(nameof(maxPointsPerTopic));
        _maxPointsPerTopic = maxPointsPerTopic;
    }

    public int MaxPointsPerTopic => _maxPointsPerTopic;

    /// <summary>Appends one batch's values to each affected topic's series, trimming to
    /// <see cref="MaxPointsPerTopic"/>. Called by <see cref="ClientSession.Ingest"/> for a live
    /// subscription; also usable directly in tests with synthetic batches.</summary>
    public void Apply(SampleBatch batch)
    {
        if (batch.Values.Count == 0) return; // tick: no new value to record

        lock (_gate)
        {
            foreach (var tv in batch.Values)
            {
                if (!_series.TryGetValue(tv.Topic, out var q))
                {
                    q = new Queue<HistoryPoint>(Math.Min(_maxPointsPerTopic, 64));
                    _series[tv.Topic] = q;
                }

                q.Enqueue(new HistoryPoint(
                    batch.T,
                    ClientValue.FromProto(tv),
                    tv.HasConfidence ? tv.Confidence : null,
                    tv.Validity,
                    tv.HasEvidenceSince ? tv.EvidenceSince : null));

                while (q.Count > _maxPointsPerTopic) q.Dequeue();
            }
        }
    }

    public IReadOnlyList<HistoryPoint> Get(uint topicId)
    {
        lock (_gate)
        {
            return _series.TryGetValue(topicId, out var q) ? q.ToArray() : Array.Empty<HistoryPoint>();
        }
    }

    /// <summary>Clears all retained history, e.g. after a catalog change invalidates topic ids.</summary>
    public void Clear()
    {
        lock (_gate) _series.Clear();
    }
}
