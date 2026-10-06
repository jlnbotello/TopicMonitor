using Panel.Contracts;

namespace Panel.Client;

/// <summary>
/// Snapshot of one topic's latest known value, confidence, validity and timestamps, as kept by
/// <see cref="TopicStateStore"/>. Timestamps are copied verbatim from the <c>SampleBatch</c> that produced
/// this state (plan section 7: "value, confidence, validity and timestamps per topic").
/// </summary>
public sealed record TopicState(
    uint TopicId,
    ClientValue Value,
    float? Confidence,
    Validity Validity,
    long? EvidenceSince,
    long T,
    long TPrev,
    long TProcessed,
    long TPublish,
    long TUtcUs);
