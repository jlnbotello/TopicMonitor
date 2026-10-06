using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Panel.Core;

/// <summary>
/// Generic timestamped topic bus. Every source tick is published as a <see cref="Sample"/>; publish policies decide
/// which values actually reach history/subscribers, and <see cref="SetSourceInvalid"/> marks a stopped source's
/// topics Invalid. Thread-safe: <see cref="Publish"/> may be called concurrently with <see cref="Subscribe"/>.
/// </summary>
public sealed class TopicBus : ITopicBus
{
    private readonly TimeProvider _timeProvider;
    private readonly HistoryBuffer _history;

    private readonly object _registryLock = new();
    private readonly List<TopicDescriptor> _descriptors = new();
    private readonly Dictionary<string, int> _nameToId = new();
    private ulong _catalogVersion;

    private readonly Dictionary<int, TopicValue> _lastPublished = new();

    private readonly object _lock = new();
    private readonly List<Subscriber> _subscribers = new();

    private sealed class Subscriber
    {
        public required TopicFilter Filter;
        public required DeliveryMode Mode;
        public required Channel<Sample> Channel;
    }

    public TopicBus(TimeProvider timeProvider, TimeSpan? historyRetention = null)
    {
        _timeProvider = timeProvider;
        var retention = historyRetention ?? TimeSpan.FromMinutes(5);
        var ticks = (long)(retention.TotalSeconds * timeProvider.TimestampFrequency);
        _history = new HistoryBuffer(Math.Max(ticks, 1));
    }

    public TopicHandle Register(TopicDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        lock (_registryLock)
        {
            if (_nameToId.ContainsKey(descriptor.Name))
                throw new DuplicateTopicException(descriptor.Name);

            var id = _descriptors.Count;
            _descriptors.Add(descriptor);
            _nameToId[descriptor.Name] = id;
            _catalogVersion++;
            return new TopicHandle(id);
        }
    }

    public TopicDescriptor GetDescriptor(TopicHandle handle)
    {
        lock (_registryLock)
        {
            if (handle.Id < 0 || handle.Id >= _descriptors.Count)
                throw new ArgumentOutOfRangeException(nameof(handle), "Unknown topic handle.");
            return _descriptors[handle.Id];
        }
    }

    public TopicHandle? TryGetHandle(string topicName)
    {
        lock (_registryLock)
        {
            return _nameToId.TryGetValue(topicName, out var id) ? new TopicHandle(id) : null;
        }
    }

    public TopicCatalogSnapshot GetCatalog()
    {
        lock (_registryLock)
        {
            var entries = _descriptors.Select((d, i) => new TopicCatalogEntry(new TopicHandle(i), d)).ToList();
            return new TopicCatalogSnapshot(_catalogVersion, entries);
        }
    }

    public void Publish(Sample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);

        var filtered = ApplyPublishPolicies(sample.Values);
        var effective = filtered is null ? sample : sample with { Values = filtered };

        // History add and the subscriber-list snapshot happen under one lock, atomically with Subscribe's own
        // registration+replay-snapshot below, so a subscriber can never see a sample twice (once via replay, once
        // live) nor miss one that lands in the gap between registering and reading history.
        List<Subscriber> snapshot;
        lock (_lock)
        {
            _history.Add(effective);
            snapshot = _subscribers.ToList();
        }

        foreach (var sub in snapshot)
        {
            var projected = ProjectForFilter(effective, sub.Filter);
            if (projected is not null)
                PushToSubscriber(sub, projected);
        }
    }

    public void SetSourceInvalid(SourceId source)
    {
        List<TopicValue> invalidValues;
        lock (_registryLock)
        {
            invalidValues = _descriptors
                .Select((d, i) => (d, i))
                .Where(x => x.d.Producer == source.Value)
                .Select(x => TopicValue.Invalid(new TopicHandle(x.i)))
                .ToList();
        }

        if (invalidValues.Count == 0) return;

        var now = _timeProvider.GetTimestamp();
        Publish(new Sample(source, 0, now, now, now, invalidValues));
    }

    /// <summary>
    /// Registers the subscriber and captures its history replay synchronously, then returns a lazy stream of it.
    /// Registration must not be deferred to first enumeration: a C# async-iterator method does not run any of its
    /// body — including subscriber registration — until the caller's first <c>MoveNextAsync()</c>, so if this method
    /// were itself an iterator, any <see cref="Publish"/> between calling <c>Subscribe</c> and first enumerating
    /// would be silently lost (and, for a <see cref="DeliveryMode.Lossless"/> replay, could hang the caller forever
    /// waiting for a sample that was never delivered). Doing the registration here, eagerly, avoids that gap.
    /// </summary>
    public IAsyncEnumerable<Sample> Subscribe(
        TopicFilter filter,
        long? fromTime,
        DeliveryMode mode,
        CancellationToken cancellationToken = default)
    {
        var channel = mode == DeliveryMode.Lossless
            ? Channel.CreateBounded<Sample>(new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false })
            : Channel.CreateBounded<Sample>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });

        var subscriber = new Subscriber { Filter = filter, Mode = mode, Channel = channel };

        List<Sample> replay = new();
        lock (_lock)
        {
            _subscribers.Add(subscriber);
            if (fromTime.HasValue)
            {
                foreach (var s in _history.Since(fromTime.Value))
                {
                    var projected = ProjectForFilter(s, filter);
                    if (projected is not null)
                        replay.Add(projected);
                }
            }
        }

        return StreamAsync(subscriber, replay, cancellationToken);
    }

    private async IAsyncEnumerable<Sample> StreamAsync(
        Subscriber subscriber,
        List<Sample> replay,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            foreach (var sample in replay)
                yield return sample;

            while (await subscriber.Channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (subscriber.Channel.Reader.TryRead(out var sample))
                    yield return sample;
            }
        }
        finally
        {
            lock (_lock) _subscribers.Remove(subscriber);
        }
    }

    private void PushToSubscriber(Subscriber sub, Sample sample)
    {
        if (sub.Channel.Writer.TryWrite(sample))
            return;

        if (sub.Mode == DeliveryMode.Lossless)
        {
            sub.Channel.Writer.TryComplete(new SubscriberOverflowException());
            lock (_lock) _subscribers.Remove(sub);
        }
    }

    /// <summary>Projects a sample onto a subscriber's filter: ticks (empty values) pass through; a value sample
    /// passes only the matching values, or is dropped entirely if none match.</summary>
    private Sample? ProjectForFilter(Sample sample, TopicFilter filter)
    {
        if (sample.Values.Count == 0)
            return sample;

        List<TopicValue>? matching = null;
        for (var i = 0; i < sample.Values.Count; i++)
        {
            var tv = sample.Values[i];
            if (filter.Matches(GetDescriptor(tv.Topic).Name))
            {
                matching ??= new List<TopicValue>(sample.Values.Count);
                matching.Add(tv);
            }
        }

        if (matching is null) return null;
        return matching.Count == sample.Values.Count ? sample : sample with { Values = matching };
    }

    /// <summary>Applies each topic's declared publish policy, dropping values that don't qualify. Returns null if nothing was dropped.</summary>
    private List<TopicValue>? ApplyPublishPolicies(IReadOnlyList<TopicValue> values)
    {
        if (values.Count == 0) return null;

        List<TopicValue>? kept = null;
        for (var i = 0; i < values.Count; i++)
        {
            var tv = values[i];
            var policy = GetDescriptor(tv.Topic).EffectivePolicy;

            bool include;
            lock (_registryLock)
            {
                var hasPrev = _lastPublished.TryGetValue(tv.Topic.Id, out var prev);
                include = policy.Kind switch
                {
                    PublishPolicyKind.Every => true,
                    PublishPolicyKind.OnChange => !hasPrev || prev!.Validity != tv.Validity || !prev.Value.Equals(tv.Value),
                    PublishPolicyKind.Deadband => !hasPrev || prev!.Validity != tv.Validity || tv.Validity == Validity.Invalid
                        || Delta(prev.Value, tv.Value) >= policy.DeadbandThreshold,
                    _ => true,
                };
                if (include) _lastPublished[tv.Topic.Id] = tv;
            }

            if (!include)
            {
                kept ??= new List<TopicValue>(values.Take(i));
            }
            else
            {
                kept?.Add(tv);
            }
        }

        return kept;
    }

    private static double Delta(Value a, Value b)
    {
        if (a.Kind != b.Kind) return double.MaxValue;
        return a.Kind switch
        {
            ValueKind.Float => Math.Abs(a.AsFloat - b.AsFloat),
            ValueKind.Int => Math.Abs(a.AsInt - b.AsInt),
            ValueKind.Vec => Math.Sqrt(a.AsVec.Zip(b.AsVec, (x, y) => (x - y) * (x - y)).Sum()),
            _ => a.Equals(b) ? 0 : double.MaxValue,
        };
    }
}
