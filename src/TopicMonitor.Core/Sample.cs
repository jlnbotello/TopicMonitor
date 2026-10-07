namespace TopicMonitor.Core;

public readonly record struct TopicHandle(int Id)
{
    public override string ToString() => $"Topic#{Id}";
}

public readonly record struct SourceId(string Value)
{
    public override string ToString() => Value;
}

public enum Validity
{
    Valid,
    Invalid,
}

public sealed record TopicValue(
    TopicHandle Topic,
    Value Value,
    float? Confidence,
    Validity Validity,
    long? EvidenceSince)
{
    public static TopicValue Invalid(TopicHandle topic, long? evidenceSince = null) =>
        new(topic, default, null, Core.Validity.Invalid, evidenceSince);
}

/// <summary>One atomic sample: all values observed at one instant. An empty <see cref="Values"/> list is a tick.</summary>
public sealed record Sample(
    SourceId Source,
    ulong Seq,
    long T,
    long TPrev,
    long TProcessed,
    IReadOnlyList<TopicValue> Values);
