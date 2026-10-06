# Writing a new topic source

A "source" is anything that registers topics on the bus and publishes `Sample`s to them — the scenario
file reader (`Panel.Sources.File`) is one; a future camera reader (see `docs/plan.md` section 11)
would be another. This doc covers the generic part: everything a source needs from `Panel.Core`,
independent of where the data actually comes from.

All of this lives in `Panel.Core` — read `src/Panel.Core/*.cs` directly alongside this doc; it's short.

## The three things a source does

1. **Register** each topic it owns, once, via `ITopicBus.Register`.
2. **Publish** samples as new data arrives.
3. **Invalidate** its topics when it stops (so subscribers see `Invalid`, not stale data).

```csharp
public interface ITopicBus
{
    TopicHandle Register(TopicDescriptor descriptor);          // throws DuplicateTopicException if the name is taken
    void Publish(Sample sample);
    void SetSourceInvalid(SourceId source);
    IAsyncEnumerable<Sample> Subscribe(TopicFilter filter, long? fromTime, DeliveryMode mode, CancellationToken ct = default);
    TopicCatalogSnapshot GetCatalog();
    TopicDescriptor GetDescriptor(TopicHandle handle);
    TopicHandle? TryGetHandle(string topicName);
}
```

## 1. Describe your topics

```csharp
public sealed record TopicDescriptor(
    string Name,                               // e.g. "led.1.raw" — dotted, globbable
    TopicType Type,                             // Float | Int | Bool | String | Enum | Vec
    string Producer,                            // your source's name; becomes Sample.Source.Value
    TopicKind Kind = TopicKind.Raw,              // Raw for a source; Derived is for processors
    string Unit = "",
    IReadOnlyList<string>? EnumValues = null,    // Enum only
    IReadOnlyList<string>? Components = null,    // Vec only, e.g. ["r","g","b"]
    PublishPolicy? Policy = null,                // Every (default) | OnChange | Deadband(threshold)
    IReadOnlyList<string>? DerivedFrom = null);
```

Register once per topic, at startup:

```csharp
var handle = bus.Register(new TopicDescriptor("sensor.temp", TopicType.Float, Producer: "my-source"));
```

A name is already taken by someone else → `Register` throws `DuplicateTopicException`. Every topic has
exactly one producer (plan section 4/5) — catch this and fail loudly rather than silently stealing the name.

**Publish policy** decides which values the bus actually keeps/forwards, independent of how often you
publish: `Every` forwards everything, `OnChange` drops a value equal to the last one that got through,
`Deadband(threshold)` drops small numeric changes. This runs inside the bus, so you don't have to
de-duplicate yourself — just publish on every tick and let the policy filter.

## 2. Publish samples

```csharp
public sealed record Sample(SourceId Source, ulong Seq, long T, long TPrev, long TProcessed, IReadOnlyList<TopicValue> Values);
public sealed record TopicValue(TopicHandle Topic, Value Value, float? Confidence, Validity Validity, long? EvidenceSince);
```

- `Source` is your producer's name, wrapped — `new SourceId("my-source")`.
- `T`/`TPrev`/`TProcessed` are timestamps in `TimeProvider.GetTimestamp()`'s tick domain (QPC-equivalent;
  see "Time base" below) — **not** wall-clock `DateTime`.
- `Values` is every topic you own, every tick, even when nothing changed (plan section 5: "every source
  tick produces a sample, even without changes" — the bus's publish policy is what trims this, not you).
- An empty `Values` list is a bare tick (useful for keeping a shared time axis moving without any topic
  having a value yet).

`Value` is a small tagged union — build one with `Value.OfFloat(x)`, `Value.OfInt(x)`, `Value.OfBool(x)`,
`Value.OfString(x)`, `Value.OfEnum(name)`, or `Value.OfVec(double[])`, matching your topic's declared type.

```csharp
bus.Publish(new Sample(
    new SourceId("my-source"),
    Seq: ++seq,
    T: timeProvider.GetTimestamp(),
    TPrev: prevT,
    TProcessed: timeProvider.GetTimestamp(), // no extra processing delay for a plain source
    Values: new[] { new TopicValue(handle, Value.OfFloat(reading), Confidence: null, Validity.Valid, EvidenceSince: null) }));
```

## 3. Time base — always use `TimeProvider`, never `Stopwatch`/`DateTime` directly

Inject `TimeProvider` into your source's constructor (production gets `TimeProvider.System`; tests inject
`Microsoft.Extensions.Time.Testing.FakeTimeProvider`). If you need to tick periodically, use
`TimeProvider.CreateTimer(callback, state, dueTime, period)` — **not** `System.Threading.Timer` or
`Task.Delay`. Under `FakeTimeProvider`, a `CreateTimer` callback only fires when the test calls
`.Advance(...)`, which is what makes a source's output deterministic and testable without real delays. This
is the single most-repeated lesson across every source/processor built so far — see `ScenarioReplayer.cs`
for the canonical example.

## 4. Stopping

```csharp
public void Stop() => bus.SetSourceInvalid(new SourceId("my-source"));
```

This marks every topic your producer owns as `Validity.Invalid` on the bus (plan section 5: "a source that
stops sets all its topics to Invalid") — call it from your shutdown path (an `IHostedService.StopAsync`, a
`Dispose`, whatever fits).

## A minimal complete example

```csharp
public sealed class RandomWalkSource : IDisposable
{
    private readonly ITopicBus _bus;
    private readonly TimeProvider _time;
    private readonly SourceId _source = new("randomwalk");
    private readonly TopicHandle _handle;
    private readonly ITimer _timer;
    private readonly Random _rng = new();
    private double _value;
    private long _prevT;
    private ulong _seq;

    public RandomWalkSource(ITopicBus bus, TimeProvider time, TimeSpan period)
    {
        _bus = bus;
        _time = time;
        _handle = bus.Register(new TopicDescriptor("demo.walk", TopicType.Float, Producer: _source.Value));
        _prevT = time.GetTimestamp();
        _timer = time.CreateTimer(_ => Tick(), null, period, period);
    }

    private void Tick()
    {
        _value += _rng.NextDouble() - 0.5;
        var t = _time.GetTimestamp();
        _bus.Publish(new Sample(_source, ++_seq, t, _prevT, t,
            new[] { new TopicValue(_handle, Value.OfFloat(_value), null, Validity.Valid, null) }));
        _prevT = t;
    }

    public void Dispose()
    {
        _timer.Dispose();
        _bus.SetSourceInvalid(_source);
    }
}
```

Wire it into `Panel.Server` the same way `ScenarioLoaderHostedService`/`ScenarioFileSource` are wired in
`Program.cs` — register it (and the bus) in DI, start it as an `IHostedService`/`BackgroundService`, stop it
in `StopAsync`.

## If you're producing a *derived* topic instead (a processor)

Same `ITopicBus` surface, slightly different shape: register with `Kind: TopicKind.Derived` and
`DerivedFrom: [...]`, and instead of generating data yourself, **subscribe** to the bus for your raw input
and republish a transformed value. One important, easy-to-miss detail: subscribe with
`fromTime: long.MinValue`, not `fromTime: null` — a live-only subscribe misses anything already published
before you started (this bit every processor in this codebase at least once; see the comment on
`ColorClassifierProcessor.RunAsync` for the full story). `src/Panel.Processors/*.cs` are the three
reference implementations (color classifier, blink detector, text stabilizer) — read one of them end to
end before writing a new processor.
