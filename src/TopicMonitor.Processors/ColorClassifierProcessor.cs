using TopicMonitor.Core;
using TopicMonitor.Processors.Internal;

namespace TopicMonitor.Processors;

/// <summary>
/// "Color classifier" (plan section 5): subscribes to raw `led.N.raw` vec topics matching
/// <see cref="ColorClassifierConfig.Input"/>, and for each one registers and publishes a derived
/// `led.N.color` enum topic (name derived by replacing a trailing ".raw" with ".color").
///
/// Classification: nearest reference color by plain Euclidean distance over <see cref="ColorClassifierConfig.Components"/>
/// (v1 only supports space "rgb", for which this is exact).
///
/// Confidence: margin-ratio between the nearest distance d1 and second-nearest distance d2:
/// <c>confidence = clamp01((d2 - d1) / (d1 + d2))</c>. This is 0 when the two closest references are
/// equidistant (maximally ambiguous) and tends to 1 as the second-nearest reference gets much farther
/// away than the nearest one (e.g. an exact match, d1 = 0, gives confidence = 1 whenever a second
/// reference exists at all). With fewer than two references there is no ambiguity possible, so
/// confidence is defined as 1.
///
/// Debounce: a new color is only accepted after k consecutive raw samples classify to the same nearest
/// reference (default k = 2, see <see cref="ColorClassifierConfig.K"/>); until then the classifier holds
/// its previous output. The output sample's own T is the k-th (triggering/latest) sample's time;
/// EvidenceSince is the time of the first of the k supporting samples. See <see cref="Debouncer{T}"/> for
/// the exact rule, including single-sample-glitch rejection and bootstrap behavior.
///
/// Invalid handling: an Invalid raw sample immediately produces an Invalid `led.N.color` sample (no
/// debounce delay for invalidity) and resets the debounce state for that LED, so recovery after an
/// unknown gap requires re-establishing the color from fresh samples rather than trusting whatever was
/// confirmed before the gap.
/// </summary>
public sealed class ColorClassifierProcessor
{
    public const string ProcessorName = "color-classifier";

    private readonly ITopicBus _bus;
    private readonly ColorClassifierConfig _config;

    public ColorClassifierProcessor(ITopicBus bus, ColorClassifierConfig config)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        if (_config.References.Count == 0)
            throw new ArgumentException("At least one reference color is required.", nameof(config));
    }

    /// <summary>
    /// Resolves <see cref="ColorClassifierConfig.Input"/> against the bus's current catalog, registers one
    /// derived `*.color` topic per match, then processes raw samples until <paramref name="cancellationToken"/>
    /// is cancelled or the subscription ends. Topics registered on the bus after this call starts are not
    /// picked up (v1 scope: no dynamic catalog re-resolution).
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var filter = new TopicFilter(new[] { _config.Input });
        var entries = new Dictionary<int, LedEntry>();

        foreach (var entry in _bus.GetCatalog().Topics)
        {
            if (!filter.Matches(entry.Descriptor.Name)) continue;

            var outputName = ToColorTopicName(entry.Descriptor.Name);
            var outputHandle = _bus.Register(new TopicDescriptor(
                outputName,
                TopicType.Enum,
                ProcessorName,
                TopicKind.Derived,
                EnumValues: _config.References.Keys.ToList(),
                Policy: PublishPolicy.OnChange,
                DerivedFrom: new[] { entry.Descriptor.Name }));

            entries[entry.Handle.Id] = new LedEntry(outputHandle, new Debouncer<string>(_config.K));
        }

        if (entries.Count == 0) return;

        ulong seq = 0;
        // fromTime: long.MinValue (not null) replays everything already in history before switching to live.
        // Registration above only reserves this processor's output topics; it does not mean the bus has no raw
        // samples yet -- a source's very first tick is typically published during its own startup, strictly
        // before this RunAsync call even begins, so a live-only subscribe (fromTime: null) would deterministically
        // miss it on every run.
        await foreach (var sample in _bus.Subscribe(filter, long.MinValue, DeliveryMode.Lossless, cancellationToken).ConfigureAwait(false))
        {
            if (sample.Values.Count == 0) continue;

            List<TopicValue>? outputs = null;
            foreach (var tv in sample.Values)
            {
                if (!entries.TryGetValue(tv.Topic.Id, out var entry)) continue;

                var outTv = ProcessOne(entry, tv, sample.T, _config.References);
                if (outTv is not null)
                    (outputs ??= new List<TopicValue>()).Add(outTv);
            }

            if (outputs is { Count: > 0 })
            {
                _bus.Publish(new Sample(new SourceId(ProcessorName), seq++, sample.T, sample.TPrev, sample.TProcessed, outputs));
            }
        }
    }

    private static TopicValue? ProcessOne(LedEntry entry, TopicValue raw, long t, IReadOnlyDictionary<string, double[]> references)
    {
        if (raw.Validity == Validity.Invalid)
        {
            entry.Debouncer.Reset();
            return TopicValue.Invalid(entry.OutputHandle);
        }

        var (name, d1, d2) = Classify(references, raw.Value.AsVec);
        var confidence = Confidence(d1, d2);

        if (entry.Debouncer.Observe(name, t, out var evidenceSince))
        {
            return new TopicValue(entry.OutputHandle, Value.OfEnum(entry.Debouncer.Confirmed!), confidence, Validity.Valid, evidenceSince);
        }

        return null;
    }

    private static (string Name, double D1, double D2) Classify(IReadOnlyDictionary<string, double[]> references, double[] vec)
    {
        string? best = null;
        var bestD = double.PositiveInfinity;
        var secondD = double.PositiveInfinity;

        foreach (var (name, reference) in references)
        {
            var d = EuclideanDistance(vec, reference);
            if (d < bestD)
            {
                secondD = bestD;
                bestD = d;
                best = name;
            }
            else if (d < secondD)
            {
                secondD = d;
            }
        }

        return (best!, bestD, secondD);
    }

    private static float Confidence(double d1, double d2)
    {
        if (double.IsPositiveInfinity(d2)) return 1f; // only one reference: no ambiguity possible
        var sum = d1 + d2;
        if (sum <= 0) return 0f; // degenerate: two references at the same point, zero distance to both
        return (float)Math.Clamp((d2 - d1) / sum, 0.0, 1.0);
    }

    private static double EuclideanDistance(double[] a, double[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        double sum = 0;
        for (var i = 0; i < n; i++)
        {
            var d = a[i] - b[i];
            sum += d * d;
        }
        return Math.Sqrt(sum);
    }

    private static string ToColorTopicName(string rawTopicName) =>
        rawTopicName.EndsWith(".raw", StringComparison.Ordinal)
            ? string.Concat(rawTopicName.AsSpan(0, rawTopicName.Length - ".raw".Length), ".color")
            : rawTopicName + ".color";

    private sealed record LedEntry(TopicHandle OutputHandle, Debouncer<string> Debouncer);
}
