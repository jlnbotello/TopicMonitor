# Writing a client

`TopicMonitor.Client` wraps the gRPC `TopicService` so a caller never touches streaming types, reconnect logic, or
`catalog_version` bookkeeping directly — the WPF viewer and the integration test suite are both just
callers of `PanelClient`, nothing more privileged. This doc is everything you need to build a third one.

Read `src/TopicMonitor.Client/PanelClient.cs` directly alongside this — it's ~220 lines and every member below is
there verbatim.

## Connect, describe, subscribe

```csharp
using var client = PanelClient.ConnectTo("http://127.0.0.1:5279"); // owns its channel; disposed with the client

await client.DescribeAsync();                 // fetches + caches the catalog (client.Catalog)
await client.SubscribeAsync(
    patterns: new[] { "*" },                  // glob patterns, e.g. "led.*.color"
    mode: DeliveryMode.Lossless,               // or DeliveryMode.Latest
    fromTime: null);                           // null = live only; a tick value = replay history first
```

`SubscribeAsync` starts a background read loop and returns immediately — don't await it expecting the
stream to end; it runs until you call `StopSubscriptionAsync()` or dispose the client. Calling
`SubscribeAsync` again (new patterns/mode) replaces the running subscription.

**Lossless vs Latest**: `Lossless` buffers and guarantees delivery order, but a subscriber that falls too
far behind gets disconnected by the server (`RpcException` with `Aborted`) — `PanelClient` already catches
that and auto-resyncs (see "Reconnect" below), so you don't need to handle it yourself. `Latest` never
blocks the publisher and just coalesces to the newest value; use it for something like a live status
display where you only ever care about "now."

## Reading state and history

```csharp
TopicState? state = client.GetState("led.1.color");      // current value, by topic name
IReadOnlyList<HistoryPoint> hist = client.GetHistory("led.1.color"); // time series, oldest first

if (state is { Validity: Validity.Valid } s)
{
    var v = s.Value;             // ClientValue — a tagged union, see below
    var t = s.T;                 // tick timestamp (TimeProvider domain, not wall clock)
    var conf = s.Confidence;     // float? — present for derived topics that report confidence
}
```

Both are resolved by topic *name* through the cached catalog — if you'd rather work by numeric id directly
(e.g. you're iterating `client.Catalog.Topics` yourself), use `client.State.Get(id)` /
`client.History.Get(id)` instead; `GetState`/`GetHistory` are just `ResolveTopicId(name)` + those calls.

`HistoryStore` keeps a bounded count per topic (default 5000 points, configurable via
`ConnectTo(..., historyCapacityPerTopic: N)`) — enough for several minutes at typical rates, but it is not
unbounded, so don't rely on it for anything beyond "the recent past for a UI."

## Reading a value out of `ClientValue`

`TopicMonitor.Client` has no dependency on `TopicMonitor.Core`, so values come back as `ClientValue`, not
`TopicMonitor.Core.Value` — same shape, different type:

```csharp
switch (state.Value.Kind)
{
    case ClientValueKind.Float:  Console.WriteLine(state.Value.AsFloat); break;
    case ClientValueKind.Bool:   Console.WriteLine(state.Value.AsBool); break;
    case ClientValueKind.String: Console.WriteLine(state.Value.AsString); break;
    case ClientValueKind.Enum:
        // Enum values come back as a wire index; resolve the name yourself via the catalog:
        var info = client.Catalog!.Topics.First(t => t.Name == "led.1.color");
        Console.WriteLine(info.EnumValues[(int)state.Value.AsEnumIndex]);
        break;
    case ClientValueKind.Vec:    Console.WriteLine(string.Join(",", state.Value.AsVec)); break;
}
```

## Reacting to live updates

```csharp
client.SampleReceived += (_, batch) =>
{
    // batch is the raw proto SampleBatch — fires once per batch received, after it's already been
    // applied to State/History, so GetState/GetHistory reflect it by the time your handler runs.
};

client.CatalogChanged += (_, catalog) =>
{
    // Fires on the first DescribeAsync and again any time catalog_version bumps (e.g. a scenario reload
    // added/removed topics). Re-read client.Catalog.Topics if you cache anything derived from it.
};
```

If you're driving a UI (WPF, etc.), marshal `SampleReceived` onto your UI dispatcher yourself — it fires on
whatever thread the read loop is running on, not the UI thread.

## Reconnect — already handled, know what it does

`PanelClient` automatically reconnects and resyncs on:
- a transport fault or the server disconnecting a `Lossless` subscriber that fell behind, and
- a detected `seq` gap (two batches from the same source where `seq` isn't `prev + 1`).

Both resync the same way: re-`DescribeAsync()` (the catalog may have moved on while disconnected), then
resubscribe with `fromTime` = the last sample's `t` you actually received, so you pick up exactly where you
left off rather than missing or duplicating data. You don't need to write any of this — it's worth knowing
about only so you're not surprised by a brief gap in `SampleReceived` during a real disconnect.

## Latency

```csharp
var summary = client.Latency.Summary(TimeSpan.FromSeconds(10), tickFrequency: TimeProvider.System.TimestampFrequency);
if (summary is { } s) Console.WriteLine($"p50 {s.P50Ticks} ticks, max {s.MaxTicks} ticks (last 10s)");
```

This is `recv - t_processed` per batch — valid as a direct subtraction only because client and server
share one clock domain on the same machine (plan section 7). `GetTimeAsync()` /
`EstimateTimeOffsetAsync()` exist for a future remote client that needs an offset estimate instead; don't
use them on localhost, they're not needed there.

## A minimal complete example

```csharp
await using var client = PanelClient.ConnectTo("http://127.0.0.1:5279");
await client.SubscribeAsync(new[] { "led.1.color" }, DeliveryMode.Lossless);

client.SampleReceived += (_, _) =>
{
    if (client.GetState("led.1.color") is { } s)
        Console.WriteLine($"led.1.color = {s.Value} (valid: {s.Validity == Validity.Valid})");
};

await Task.Delay(Timeout.Infinite); // keep the read loop alive
```
