using System.Collections.Concurrent;
using TopicMonitor.Contracts;

namespace TopicMonitor.Client;

/// <summary>
/// Tracks the latest known state for every topic the client has seen, keyed by the proto topic id
/// (<c>TopicValue.Topic</c> / <c>TopicInfo.Id</c>). Thread-safe: <see cref="Apply"/> is called from the
/// background read loop while a UI thread reads <see cref="Get"/>/<see cref="Snapshot"/>.
/// <para>
/// An empty <c>SampleBatch.Values</c> is a tick (plan section 5: "empty Values = tick") and leaves existing
/// state untouched.
/// </para>
/// </summary>
public sealed class TopicStateStore
{
    private readonly ConcurrentDictionary<uint, TopicState> _states = new();

    public bool TryGet(uint topicId, out TopicState? state) => _states.TryGetValue(topicId, out state);

    public TopicState? Get(uint topicId) => _states.TryGetValue(topicId, out var s) ? s : null;

    public IReadOnlyCollection<TopicState> Snapshot() => _states.Values.ToArray();

    /// <summary>Applies one batch's values to the store. Called by <see cref="ClientSession.Ingest"/> for
    /// a live subscription; also usable directly in tests with synthetic batches.</summary>
    public void Apply(SampleBatch batch)
    {
        foreach (var tv in batch.Values)
        {
            _states[tv.Topic] = new TopicState(
                tv.Topic,
                ClientValue.FromProto(tv),
                tv.HasConfidence ? tv.Confidence : null,
                tv.Validity,
                tv.HasEvidenceSince ? tv.EvidenceSince : null,
                batch.T,
                batch.TPrev,
                batch.TProcessed,
                batch.TPublish,
                batch.TUtcUs);
        }
    }
}
