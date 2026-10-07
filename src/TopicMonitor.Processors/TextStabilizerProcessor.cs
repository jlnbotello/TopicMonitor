using TopicMonitor.Core;
using TopicMonitor.Processors.Internal;

namespace TopicMonitor.Processors;

/// <summary>
/// "Text stabilizer": subscribes to raw `display.lineN.raw` string topics matching
/// <see cref="TextStabilizerConfig.Input"/>, and for each one registers and publishes a derived
/// `display.lineN.text` string topic (name derived by replacing a trailing ".raw" with ".text").
///
/// Uses the exact same k-sample debounce as the color classifier (see <see cref="Debouncer{T}"/>): a new
/// text is only accepted after k consecutive equal raw samples; the output sample's T is the k-th
/// (triggering) sample's time, and EvidenceSince is the first of the k supporting samples' time.
///
/// Invalid handling: an Invalid raw sample immediately produces an Invalid `*.text` sample and resets the
/// debounce state, mirroring the color classifier's rule (recovery after an unknown gap re-bootstraps from
/// fresh samples rather than trusting stale pre-gap state).
///
/// Confidence is not meaningful for this processor (no reference/margin concept), so it is always null.
/// </summary>
public sealed class TextStabilizerProcessor
{
    public const string ProcessorName = "text-stabilizer";

    private readonly ITopicBus _bus;
    private readonly TextStabilizerConfig _config;

    public TextStabilizerProcessor(ITopicBus bus, TextStabilizerConfig config)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Resolves <see cref="TextStabilizerConfig.Input"/> against the bus's current catalog, registers one
    /// derived `*.text` topic per match, then processes raw samples until <paramref name="cancellationToken"/>
    /// is cancelled or the subscription ends. Topics registered on the bus after this call starts are not
    /// picked up (v1 scope: no dynamic catalog re-resolution).
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var filter = new TopicFilter(new[] { _config.Input });
        var entries = new Dictionary<int, LineEntry>();

        foreach (var entry in _bus.GetCatalog().Topics)
        {
            if (!filter.Matches(entry.Descriptor.Name)) continue;

            var outputName = ToTextTopicName(entry.Descriptor.Name);
            var outputHandle = _bus.Register(new TopicDescriptor(
                outputName,
                TopicType.String,
                ProcessorName,
                TopicKind.Derived,
                Policy: PublishPolicy.OnChange,
                DerivedFrom: new[] { entry.Descriptor.Name }));

            entries[entry.Handle.Id] = new LineEntry(outputHandle, new Debouncer<string>(_config.K));
        }

        if (entries.Count == 0) return;

        ulong seq = 0;
        // fromTime: long.MinValue (not null) replays everything already in history before switching to live --
        // see the matching comment in ColorClassifierProcessor.RunAsync for why a live-only subscribe would
        // deterministically miss its input's first sample.
        await foreach (var sample in _bus.Subscribe(filter, long.MinValue, DeliveryMode.Lossless, cancellationToken).ConfigureAwait(false))
        {
            if (sample.Values.Count == 0) continue;

            List<TopicValue>? outputs = null;
            foreach (var tv in sample.Values)
            {
                if (!entries.TryGetValue(tv.Topic.Id, out var entry)) continue;

                var outTv = ProcessOne(entry, tv, sample.T);
                if (outTv is not null)
                    (outputs ??= new List<TopicValue>()).Add(outTv);
            }

            if (outputs is { Count: > 0 })
            {
                _bus.Publish(new Sample(new SourceId(ProcessorName), seq++, sample.T, sample.TPrev, sample.TProcessed, outputs));
            }
        }
    }

    private static TopicValue? ProcessOne(LineEntry entry, TopicValue raw, long t)
    {
        if (raw.Validity == Validity.Invalid)
        {
            entry.Debouncer.Reset();
            return TopicValue.Invalid(entry.OutputHandle);
        }

        if (entry.Debouncer.Observe(raw.Value.AsString, t, out var evidenceSince))
        {
            return new TopicValue(entry.OutputHandle, Value.OfString(entry.Debouncer.Confirmed!), null, Validity.Valid, evidenceSince);
        }

        return null;
    }

    private static string ToTextTopicName(string rawTopicName) =>
        rawTopicName.EndsWith(".raw", StringComparison.Ordinal)
            ? string.Concat(rawTopicName.AsSpan(0, rawTopicName.Length - ".raw".Length), ".text")
            : rawTopicName + ".text";

    private sealed record LineEntry(TopicHandle OutputHandle, Debouncer<string> Debouncer);
}
