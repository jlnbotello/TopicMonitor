using TopicMonitor.Core;

namespace TopicMonitor.Sources.File;

/// <summary>One topic's resolved state at a tick: value, validity, and held confidence.</summary>
public sealed record ScenarioResolvedValue(Value Value, Validity Validity, float? Confidence);

/// <summary>
/// Resolves relative times, quantizes every assignment up to the next source tick, and evaluates, for any tick
/// index, the set of active topic values (holding until changed, generators evaluated as a function of elapsed
/// time since they started, `@loop` wraparound via modulo, and seeded deterministic Gaussian noise per plan
/// section 4). <see cref="Evaluate"/> is a pure function of the tick index: noise is derived from a hash of
/// (seed, topic, tick, component) rather than from sequential RNG draws, so results don't depend on call order
/// and are reproducible given the same seed.
/// </summary>
public sealed class ScenarioExpander
{
    private readonly ScenarioDocument _doc;
    private readonly int _noiseSeed;
    private readonly double _tickPeriodMs;
    private readonly long _loopLengthTicks;
    private readonly Dictionary<string, ScenarioTopicDirective> _topicsByName;
    private readonly Dictionary<string, List<(long Tick, ScenarioAssign Assign)>> _eventsByTopic;
    private readonly List<(TopicFilter Filter, double Sigma)> _noise;

    public double TickPeriodMs => _tickPeriodMs;
    public int Rate { get; }
    public bool Loop => _doc.Loop;

    /// <summary>Builds the expander for one '@source' block's own topics and tick rate. A file may declare
    /// several sources at different rates (e.g. a fast one for LEDs, a slower one for display text); the
    /// sample-line time grammar (absolute/relative ms) is shared and resolved once regardless of which
    /// source is being built, but each source quantizes those shared absolute times to *its own* tick
    /// period, and <see cref="Evaluate"/> only ever reports values for topics this source owns -- a sample
    /// line that assigns to topics from several sources in one line is split between their respective
    /// expanders automatically, by topic ownership, not by any special-casing here.</summary>
    public ScenarioExpander(ScenarioDocument doc, string sourceName, int noiseSeed = 0)
    {
        _doc = doc;
        _noiseSeed = noiseSeed;

        var source = doc.Sources.FirstOrDefault(s => s.Name == sourceName)
            ?? throw new ScenarioValidationException($"Unknown source '{sourceName}'.");
        if (source.Rate <= 0)
            throw new ScenarioValidationException($"'@source {source.Name}' has a non-positive rate ({source.Rate}).");

        Rate = source.Rate;
        _tickPeriodMs = 1000.0 / source.Rate;
        _topicsByName = doc.Topics.Where(t => t.SourceName == sourceName).ToDictionary(t => t.Name);
        _eventsByTopic = _topicsByName.Keys.ToDictionary(k => k, _ => new List<(long, ScenarioAssign)>());

        long prevAbsMs = 0;
        long maxAbsMs = 0;
        foreach (var sampleLine in doc.Samples)
        {
            var absMs = sampleLine.Time.Relative ? prevAbsMs + sampleLine.Time.ToMilliseconds() : sampleLine.Time.ToMilliseconds();
            prevAbsMs = absMs;
            if (absMs > maxAbsMs) maxAbsMs = absMs;
            var tick = QuantizeToTick(absMs, _tickPeriodMs);

            foreach (var assign in sampleLine.Assigns)
            {
                if (_eventsByTopic.TryGetValue(assign.Topic, out var events))
                    events.Add((tick, assign));
                else if (!doc.Topics.Any(t => t.Name == assign.Topic))
                    throw new ScenarioValidationException(
                        $"Line {assign.Line}, column {assign.Column}: topic '{assign.Topic}' was not declared with '@topic'.");
                // else: declared, but owned by a different '@source' -- that source's own expander handles it.
            }
        }

        foreach (var events in _eventsByTopic.Values)
            events.Sort((a, b) => a.Tick.CompareTo(b.Tick));

        // The loop period is anchored to the file's shared last-event time (maxAbsMs), not to this source's
        // own last tick, so every source wraps back to t=0 at the same real-world instant even though that
        // instant falls on a different tick index for each source's own rate. The last explicit event must
        // still be observable once before the timeline wraps, so the loop period spans [0, maxTick]
        // inclusive - maxTick + 1 distinct tick states.
        _loopLengthTicks = QuantizeToTick(maxAbsMs, _tickPeriodMs) + 1;
        _noise = doc.Noises.Select(nd => (new TopicFilter(new[] { nd.Glob }), nd.Sigma)).ToList();

        // Validate every literal against its topic's declared type up front, so a bad file never gets this far
        // silently - the loader surfaces this as a load-time ScenarioValidationException.
        foreach (var (topicName, events) in _eventsByTopic)
        {
            var directive = _topicsByName[topicName];
            foreach (var (_, assign) in events)
                ValidateNode(assign.Value, directive);
        }
    }

    /// <summary>Rounds an absolute time (ms) up to the next source tick index ("quantized up to the next source tick").</summary>
    private static long QuantizeToTick(long absMs, double tickPeriodMs)
    {
        const double epsilon = 1e-6;
        return (long)Math.Ceiling(absMs / tickPeriodMs - epsilon);
    }

    /// <summary>The set of resolved topic values active at <paramref name="tickIndex"/>. Topics with no event yet
    /// (before their first assignment) come back Invalid. Pure function of <paramref name="tickIndex"/>.</summary>
    public IReadOnlyDictionary<string, ScenarioResolvedValue> Evaluate(long tickIndex)
    {
        var teff = Loop && _loopLengthTicks > 0 ? tickIndex % _loopLengthTicks : tickIndex;
        var result = new Dictionary<string, ScenarioResolvedValue>(_topicsByName.Count);

        foreach (var (topicName, directive) in _topicsByName)
        {
            var events = _eventsByTopic[topicName];
            (long Tick, ScenarioAssign Assign)? active = null;
            foreach (var e in events)
            {
                if (e.Tick <= teff) active = e;
                else break;
            }

            if (active is null)
            {
                result[topicName] = new ScenarioResolvedValue(default, Validity.Invalid, null);
                continue;
            }

            var (eventTick, assign) = active.Value;
            var elapsedTicks = teff - eventTick;
            var elapsedMs = elapsedTicks * _tickPeriodMs;

            var (value, validity) = Resolve(assign.Value, directive, elapsedTicks, elapsedMs);
            if (validity == Validity.Valid)
                value = ApplyNoise(value, topicName, tickIndex);

            var confidence = assign.Confidence.HasValue ? (float)assign.Confidence.Value : (float?)null;
            result[topicName] = new ScenarioResolvedValue(value, validity, confidence);
        }

        return result;
    }

    private static void ValidateNode(ScenarioValueNode node, ScenarioTopicDirective directive)
    {
        switch (node)
        {
            case ScenarioInvalidNode:
                return;
            case ScenarioLiteralNode lit:
                ToCoreValue(lit.Literal, directive);
                return;
            case ScenarioBlinkNode b:
                ValidateNode(b.V1, directive);
                ValidateNode(b.V2, directive);
                return;
            case ScenarioRampNode r:
                ValidateNode(r.V1, directive);
                ValidateNode(r.V2, directive);
                return;
            case ScenarioFlickerNode f:
                ValidateNode(f.V1, directive);
                ValidateNode(f.V2, directive);
                return;
        }
    }

    private (Value, Validity) Resolve(ScenarioValueNode node, ScenarioTopicDirective directive, long elapsedTicks, double elapsedMs)
    {
        switch (node)
        {
            case ScenarioInvalidNode:
                return (default, Validity.Invalid);

            case ScenarioLiteralNode lit:
                return (ToCoreValue(lit.Literal, directive), Validity.Valid);

            case ScenarioBlinkNode b:
            {
                var period = 1000.0 / b.FrequencyHz;
                var phase = elapsedMs % period;
                var chosen = phase < b.Duty * period ? b.V1 : b.V2;
                return Resolve(chosen, directive, elapsedTicks, elapsedMs);
            }

            case ScenarioRampNode r:
            {
                var frac = r.DurationMs <= 0 ? 1.0 : Math.Clamp(elapsedMs / r.DurationMs, 0.0, 1.0);
                var (v1, val1) = Resolve(r.V1, directive, 0, 0);
                var (v2, val2) = Resolve(r.V2, directive, 0, 0);
                if (val1 == Validity.Invalid || val2 == Validity.Invalid) return (default, Validity.Invalid);
                return (Lerp(v1, v2, frac, directive.Type), Validity.Valid);
            }

            case ScenarioFlickerNode f:
            {
                var flipIndex = f.Frames <= 0 ? 0 : elapsedTicks / f.Frames;
                var chosen = flipIndex % 2 == 0 ? f.V1 : f.V2;
                return Resolve(chosen, directive, elapsedTicks, elapsedMs);
            }

            default:
                throw new InvalidOperationException("Unknown scenario value node.");
        }
    }

    private static Value ToCoreValue(ScenarioLiteral lit, ScenarioTopicDirective directive)
    {
        switch (directive.Type)
        {
            case ScenarioTopicType.Float when lit.Kind == ScenarioLiteralKind.Number:
                return Value.OfFloat(lit.Number);
            case ScenarioTopicType.Int when lit.Kind == ScenarioLiteralKind.Number:
                return Value.OfInt((long)lit.Number);
            case ScenarioTopicType.Bool when lit.Kind == ScenarioLiteralKind.Bool:
                return Value.OfBool(lit.Bool);
            case ScenarioTopicType.String when lit.Kind == ScenarioLiteralKind.String:
                return Value.OfString(lit.Text!);
            case ScenarioTopicType.Enum when lit.Kind is ScenarioLiteralKind.Ident or ScenarioLiteralKind.String:
                if (directive.EnumValues is not null && !directive.EnumValues.Contains(lit.Text))
                    throw new ScenarioValidationException(
                        $"Topic '{directive.Name}': enum value '{lit.Text}' is not one of [{string.Join(", ", directive.EnumValues)}].");
                return Value.OfEnum(lit.Text!);
            case ScenarioTopicType.Vec when lit.Kind == ScenarioLiteralKind.Vector:
                if (directive.Components is not null && lit.Vector!.Length != directive.Components.Count)
                    throw new ScenarioValidationException(
                        $"Topic '{directive.Name}': value has {lit.Vector!.Length} components, expected {directive.Components.Count}.");
                return Value.OfVec((double[])lit.Vector!.Clone());
            default:
                throw new ScenarioValidationException(
                    $"Topic '{directive.Name}' is declared as {directive.Type}, but its value doesn't match (kind: {lit.Kind}).");
        }
    }

    private static Value Lerp(Value a, Value b, double frac, ScenarioTopicType type) => type switch
    {
        ScenarioTopicType.Float => Value.OfFloat(a.AsFloat + (b.AsFloat - a.AsFloat) * frac),
        ScenarioTopicType.Int => Value.OfInt((long)Math.Round(a.AsInt + (b.AsInt - a.AsInt) * frac)),
        ScenarioTopicType.Vec => Value.OfVec(a.AsVec.Zip(b.AsVec, (x, y) => x + (y - x) * frac).ToArray()),
        _ => throw new ScenarioValidationException($"Generator 'ramp' is not supported for type '{type}'."),
    };

    private Value ApplyNoise(Value value, string topicName, long tickIndex)
    {
        var sigma = 0.0;
        var any = false;
        foreach (var (filter, s) in _noise)
        {
            if (filter.Matches(topicName)) { sigma = s; any = true; break; }
        }
        if (!any || sigma <= 0) return value;

        switch (value.Kind)
        {
            case ValueKind.Float:
                return Value.OfFloat(value.AsFloat + Gaussian(topicName, tickIndex, 0, sigma));
            case ValueKind.Int:
                return Value.OfInt((long)Math.Round(value.AsInt + Gaussian(topicName, tickIndex, 0, sigma)));
            case ValueKind.Vec:
            {
                var src = value.AsVec;
                var noisy = new double[src.Length];
                for (var i = 0; i < src.Length; i++) noisy[i] = src[i] + Gaussian(topicName, tickIndex, i, sigma);
                return Value.OfVec(noisy);
            }
            default:
                return value;
        }
    }

    /// <summary>A deterministic Gaussian draw (Box-Muller) keyed by (seed, topic, tick, component), so <see cref="Evaluate"/> stays a pure function and noise is reproducible given the same seed.</summary>
    private double Gaussian(string topicName, long tickIndex, int component, double sigma)
    {
        var seed = HashCode.Combine(_noiseSeed, topicName, tickIndex, component);
        var rnd = new Random(seed);
        var u1 = 1.0 - rnd.NextDouble();
        var u2 = rnd.NextDouble();
        var z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        return z * sigma;
    }
}
