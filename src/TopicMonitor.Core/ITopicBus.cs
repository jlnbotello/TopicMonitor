namespace TopicMonitor.Core;

public enum DeliveryMode
{
    /// <summary>Bounded queue; a subscriber that falls behind is disconnected with a resync error.</summary>
    Lossless,
    /// <summary>Coalesces to the newest state; never blocks the publisher.</summary>
    Latest,
}

public sealed record TopicCatalogEntry(TopicHandle Handle, TopicDescriptor Descriptor);

public sealed record TopicCatalogSnapshot(ulong CatalogVersion, IReadOnlyList<TopicCatalogEntry> Topics);

/// <summary>Thrown by <see cref="ITopicBus.Register"/> when a topic name is already registered (every topic has exactly one producer).</summary>
public sealed class DuplicateTopicException(string topicName)
    : InvalidOperationException($"Topic '{topicName}' is already registered.")
{
    public string TopicName { get; } = topicName;
}

/// <summary>Thrown to a Lossless subscriber that fell behind the history/queue bound; the client must resync via Describe + Subscribe(fromTime).</summary>
public sealed class SubscriberOverflowException(SourceId? source = null)
    : InvalidOperationException("Subscriber fell behind its bounded queue and must resync.")
{
    public SourceId? OverflowSource { get; } = source;
}

public interface ITopicBus
{
    /// <summary>Registers a topic descriptor and returns its handle. Throws <see cref="DuplicateTopicException"/> if the name exists.</summary>
    TopicHandle Register(TopicDescriptor descriptor);

    /// <summary>Publishes one atomic sample. Per-topic publish policies may drop unchanged values before they reach history/subscribers.</summary>
    void Publish(Sample sample);

    /// <summary>Marks every topic registered by <paramref name="source"/> as Invalid (section 5: "a source that stops sets all its topics to Invalid").</summary>
    void SetSourceInvalid(SourceId source);

    /// <summary>Streams samples matching <paramref name="filter"/>. When <paramref name="fromTime"/> is set, replays history from that time before switching to live.</summary>
    IAsyncEnumerable<Sample> Subscribe(TopicFilter filter, long? fromTime, DeliveryMode mode, CancellationToken cancellationToken = default);

    TopicCatalogSnapshot GetCatalog();

    TopicDescriptor GetDescriptor(TopicHandle handle);

    TopicHandle? TryGetHandle(string topicName);
}
