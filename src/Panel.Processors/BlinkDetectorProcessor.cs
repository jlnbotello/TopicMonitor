using Panel.Core;

namespace Panel.Processors;

/// <summary>
/// "Blink detector" (plan section 5): subscribes to derived `led.N.color` enum topics matching
/// <see cref="BlinkDetectorConfig.Input"/> (normally the color classifier's output), and for each one
/// registers and publishes a derived `led.N.state` enum topic (off/steady/blinking) and a derived
/// `led.N.freq` float topic (Hz). Names are derived by replacing a trailing ".color" with ".state"/".freq".
///
/// Terminology used here: the interval between two consecutive color changes is a "half-period" (e.g.
/// red-on duration, or off duration). Two consecutive half-periods where the color returns to where it
/// started (A -> B -> A) make up one full cycle, i.e. one period of the blink.
///
/// Rule: on every color transition, look at the last two half-periods. If they return to the same color
/// (A -> B -> A) and their durations agree within <see cref="BlinkDetectorConfig.PeriodTolerance"/>, the
/// signal is periodic: state = blinking, frequency = 1 / (half-period 1 + half-period 2). Otherwise the
/// state is off (current color is the configured <see cref="BlinkDetectorConfig.OffValue"/>) or steady
/// (any other color). State only changes on a color transition, so detecting "blinking" necessarily lags
/// the first color change by one full period — matching the table's "inherent delay >= 1 period".
///
/// EvidenceSince: for "blinking", the time of the first of the two half-periods used to confirm
/// periodicity; for "off"/"steady", the time of the most recent color change (the earliest sample
/// supporting the current steady color).
///
/// Frequency uses its topic's Deadband publish policy (plan: "frequency on change with deadband") rather
/// than processor-side filtering, matching how the bus already applies per-topic publish policies.
///
/// Invalid handling: an Invalid color sample immediately produces Invalid state and freq samples and
/// resets all transition history for that LED (periodicity must be re-established from scratch after an
/// unknown gap).
/// </summary>
public sealed class BlinkDetectorProcessor
{
    public const string ProcessorName = "blink-detector";

    private static readonly IReadOnlyList<string> States = new[] { "off", "steady", "blinking" };

    private readonly ITopicBus _bus;
    private readonly TimeProvider _timeProvider;
    private readonly BlinkDetectorConfig _config;

    public BlinkDetectorProcessor(ITopicBus bus, TimeProvider timeProvider, BlinkDetectorConfig config)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Resolves <see cref="BlinkDetectorConfig.Input"/> against the bus's current catalog, registers one
    /// derived `*.state` and `*.freq` topic pair per match, then processes color samples until
    /// <paramref name="cancellationToken"/> is cancelled or the subscription ends. Topics registered on the
    /// bus after this call starts are not picked up (v1 scope: no dynamic catalog re-resolution).
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var filter = new TopicFilter(new[] { _config.Input });
        var entries = new Dictionary<int, LedBlinkState>();

        foreach (var entry in _bus.GetCatalog().Topics)
        {
            if (!filter.Matches(entry.Descriptor.Name)) continue;

            var (stateName, freqName) = ToOutputNames(entry.Descriptor.Name);

            var stateHandle = _bus.Register(new TopicDescriptor(
                stateName,
                TopicType.Enum,
                ProcessorName,
                TopicKind.Derived,
                EnumValues: States,
                Policy: PublishPolicy.OnChange,
                DerivedFrom: new[] { entry.Descriptor.Name }));

            var freqHandle = _bus.Register(new TopicDescriptor(
                freqName,
                TopicType.Float,
                ProcessorName,
                TopicKind.Derived,
                Unit: "Hz",
                Policy: PublishPolicy.Deadband(_config.FrequencyDeadbandHz),
                DerivedFrom: new[] { entry.Descriptor.Name }));

            entries[entry.Handle.Id] = new LedBlinkState(stateHandle, freqHandle);
        }

        if (entries.Count == 0) return;

        var ticksPerSecond = _timeProvider.TimestampFrequency;

        ulong seq = 0;
        await foreach (var sample in _bus.Subscribe(filter, null, DeliveryMode.Lossless, cancellationToken).ConfigureAwait(false))
        {
            if (sample.Values.Count == 0) continue;

            List<TopicValue>? outputs = null;
            foreach (var tv in sample.Values)
            {
                if (!entries.TryGetValue(tv.Topic.Id, out var state)) continue;

                var produced = Process(state, tv, sample.T, _config, ticksPerSecond);
                if (produced is null) continue;
                (outputs ??= new List<TopicValue>()).AddRange(produced);
            }

            if (outputs is { Count: > 0 })
            {
                _bus.Publish(new Sample(new SourceId(ProcessorName), seq++, sample.T, sample.TPrev, sample.TProcessed, outputs));
            }
        }
    }

    private static TopicValue[]? Process(LedBlinkState st, TopicValue colorTv, long t, BlinkDetectorConfig config, long ticksPerSecond)
    {
        if (colorTv.Validity == Validity.Invalid)
        {
            st.LastColor = null;
            st.Transitions.Clear();
            return new[]
            {
                TopicValue.Invalid(st.StateHandle),
                TopicValue.Invalid(st.FreqHandle),
            };
        }

        var color = colorTv.Value.AsEnum;

        if (st.LastColor is null)
        {
            // First observation ever: adopt immediately, no history to assess periodicity yet.
            st.LastColor = color;
            st.Transitions.Clear();
            st.Transitions.Add((t, color));
            var state = color == config.OffValue ? "off" : "steady";
            return new[]
            {
                new TopicValue(st.StateHandle, Value.OfEnum(state), null, Validity.Valid, t),
                new TopicValue(st.FreqHandle, Value.OfFloat(0), null, Validity.Valid, t),
            };
        }

        if (color == st.LastColor)
            return null; // no transition: state/freq are unchanged, nothing new to publish

        st.Transitions.Add((t, color));
        if (st.Transitions.Count > 3) st.Transitions.RemoveAt(0);
        st.LastColor = color;

        string finalState;
        double freqHz = 0;
        long evidenceSince = t;

        if (st.Transitions.Count >= 3 && st.Transitions[^1].Color == st.Transitions[^3].Color)
        {
            var half1 = st.Transitions[^2].T - st.Transitions[^3].T;
            var half2 = st.Transitions[^1].T - st.Transitions[^2].T;
            var longer = Math.Max(half1, half2);
            var diff = Math.Abs(half1 - half2);

            if (longer > 0 && diff / (double)longer <= config.PeriodTolerance)
            {
                finalState = "blinking";
                var fullPeriodTicks = half1 + half2;
                freqHz = fullPeriodTicks > 0 ? ticksPerSecond / (double)fullPeriodTicks : 0;
                evidenceSince = st.Transitions[^3].T;
            }
            else
            {
                finalState = color == config.OffValue ? "off" : "steady";
            }
        }
        else
        {
            finalState = color == config.OffValue ? "off" : "steady";
        }

        return new[]
        {
            new TopicValue(st.StateHandle, Value.OfEnum(finalState), null, Validity.Valid, evidenceSince),
            new TopicValue(st.FreqHandle, Value.OfFloat(freqHz), null, Validity.Valid, evidenceSince),
        };
    }

    private static (string StateName, string FreqName) ToOutputNames(string colorTopicName)
    {
        var baseName = colorTopicName.EndsWith(".color", StringComparison.Ordinal)
            ? colorTopicName[..^".color".Length]
            : colorTopicName;
        return (baseName + ".state", baseName + ".freq");
    }

    private sealed class LedBlinkState
    {
        public LedBlinkState(TopicHandle stateHandle, TopicHandle freqHandle)
        {
            StateHandle = stateHandle;
            FreqHandle = freqHandle;
        }

        public TopicHandle StateHandle { get; }
        public TopicHandle FreqHandle { get; }
        public string? LastColor { get; set; }

        /// <summary>Last up to 3 (time, color) transitions, oldest first, bounded so we only ever need the
        /// last two half-periods to assess periodicity.</summary>
        public List<(long T, string Color)> Transitions { get; } = new();
    }
}
