# TopicMonitor

TopicMonitor is a generic, timestamped topic bus with a gRPC server, a .NET client library, and a WPF
timeline viewer. Producers publish typed topics; the server keeps a time-indexed history and derives new
topics from raw ones; clients subscribe, read state and history, and show many values side by side on one
shared, zoomable time axis. A replayable scenario file drives the data, so the whole pipeline runs and is
testable without any hardware attached.

![TopicMonitor viewer showing LED color/state/frequency lanes alongside display text, with two time cursors placed and a hover tooltip open](docs/images/image_01.png)

## Architecture

```
  Producer (scenario file, or your own source)
        |  publishes typed, timestamped samples
        v
  TopicMonitor.Server  ->  Topic bus (Core) ->  history buffer
        |                       |
        |                 derived-topic processors (classifier, change detector, stabilizer)
        v
  gRPC TopicService (Describe, Subscribe, GetTime)
        |
        v
  TopicMonitor.Client (catalog, state store, history, reconnect)
        |
        +--> WPF timeline viewer   (layout from layout.yaml)
        +--> your own consumer     (logger, analytics, test harness, custom UI)
```

| Project                                | Content                                                                                        |
| -------------------------------------- | ---------------------------------------------------------------------------------------------- |
| `src/TopicMonitor.Contracts`           | `.proto` definitions and generated gRPC/C# types                                               |
| `src/TopicMonitor.Core`                | Topic bus, descriptors, samples, publish policies, history                                     |
| `src/TopicMonitor.Processors`          | Derived-topic processors: nearest-reference classifier, change/rate detector, value stabilizer |
| `src/TopicMonitor.Sources.File`        | Scenario (`.scn`) parser, expander, replayer, file watcher                                     |
| `src/TopicMonitor.Server`              | Kestrel host, gRPC service, configuration                                                      |
| `src/TopicMonitor.Client`              | Catalog, subscription, state store, history, reconnect                                         |
| `src/TopicMonitor.Viewer.Layout`       | YAML layout model, template expansion, style resolution, comment-preserving save               |
| `src/TopicMonitor.Viewer.Wpf`          | Timeline control, lane renderers, interaction                                                  |
| `tests/TopicMonitor.Tests.Unit`        | Deterministic unit tests (no GUI)                                                              |
| `tests/TopicMonitor.Tests.Integration` | Real Kestrel on localhost, real gRPC clients, latency suite                                    |

The viewer and the integration tests both use `TopicMonitor.Client`, so a passing test means the viewer
receives the same data. The viewer knows nothing about a topic beyond its name, type, unit, enum values and
components.

## Design decisions

| Topic          | Decision                                                                                                                                  |
| -------------- | ----------------------------------------------------------------------------------------------------------------------------------------- |
| Runtime        | .NET 8                                                                                                                                    |
| Transport      | gRPC on Kestrel: server-streaming `Subscribe` plus unary `Describe` and `GetTime`                                                         |
| Value types    | `float`, `int`, `bool`, `string`, `enum[...]`, `vec[names]`. There is no color type; a color is just `vec[r,g,b]`                         |
| Derived topics | Computed on the server by configurable processors, published back onto the bus                                                            |
| Publish policy | Declared per topic by its producer: `Every`, `OnChange` or `Deadband(threshold)`                                                          |
| Quality        | Optional confidence per value, separate from validity (`Valid` / `Invalid`)                                                               |
| Time base      | `TimeProvider` (QPC-equivalent ticks), so timestamps are comparable across processes on one machine                                       |
| Test time      | `TimeProvider`; tests inject `FakeTimeProvider` and advance it explicitly                                                                 |
| Delivery       | `Lossless` (bounded queue; a subscriber that falls behind is disconnected with a resync code) or `Latest` (coalesces to the newest state) |
| History        | Time-based ring buffer, 5 minutes by default (configurable)                                                                               |
| Layout file    | YAML, read and written by the viewer; hand-written comments survive GUI saves                                                             |

## Scenario file (`.scn`)

A line-based text file. Directives declare sources and topics; each data line is one atomic sample at a
time offset. Values hold until changed, so a file lists only the changes.

```
@source cam rate=20                  # one tick every 50 ms; times snap to the next tick
@topic led.1.raw  vec[r,g,b]
@topic temp       float
@noise temp sigma=0.2                # optional Gaussian noise (seeded, reproducible)

0       led.1.raw=(0,0,0)  temp=20
500     led.1.raw=(240,20,20)
+100    led.1.raw=(0,0,0)            # relative: 600 ms
1500    led.1.raw=blink((240,20,20),(0,0,0),5Hz)
2000    temp=ramp(20,40,2000ms)
4500    led.1.raw=!                  # invalid until the next value
@loop                                # restart at t = 0
```

- **Values:** numbers, strings, `true`/`false`, enum names, vectors `(a,b,c)`, `!` (invalid), and the
  generators `blink(v1,v2,freq[,duty])`, `ramp(v1,v2,duration)` and `flicker(v1,v2,frames)`. A generator runs
  until the next assignment to that topic. `~` sets confidence.
- **Several sources:** a file may declare several `@source` blocks at different rates. Each owns the
  `@topic` lines after it, so fast and slow measurements can each tick at a realistic rate. `@loop` wraps every
  source back to t = 0 at the same real-world instant.
- **Errors:** parse and validation errors report line and column. A broken edit is rejected and the last valid
  scenario keeps running. Editing the file reloads it live.

## Derived-topic processors

| Processor                    | Input → output                                     | Rule                                                                                              | Delay         |
| ---------------------------- | -------------------------------------------------- | ------------------------------------------------------------------------------------------------- | ------------- |
| Nearest-reference classifier | `*.raw` (vec) → `*.color` (enum) + confidence      | Nearest named reference in the configured space; confidence from the margin to the second-nearest | k − 1 samples |
| Change/rate detector         | `*.color` (enum) → `*.state` (enum), `*.freq` (Hz) | State from the last two periods; frequency published through a deadband                           | ≥ 1 period    |
| Value stabilizer             | `*.raw` (string) → `*.text`                        | A new value is accepted only after k equal consecutive samples                                    | k − 1 samples |

`k` defaults to 2. A derived value is stamped with the time of the sample that triggered it, and
`evidence_since` points at the first supporting sample. The classifier's references are configuration:

```yaml
color:
  - input: led.*.raw
    space: rgb
    components: [r, g, b]
    references:
      off:    [0, 0, 0]
      red:    [240, 20, 20]
      green:  [20, 230, 30]
      yellow: [230, 200, 20]
```

## gRPC API (v1)

```proto
service TopicService {
  rpc Describe(DescribeRequest) returns (TopicCatalog);            // topics, types, units, enum values, components
  rpc Subscribe(SubscribeRequest) returns (stream SampleBatch);    // patterns, mode, optional from_time replay
  rpc GetTime(TimeRequest) returns (TimeReply);                    // server clock and tick frequency
}
```

- `Subscribe` takes glob patterns such as `led.*.state`, a delivery mode, and an optional `from_time`. With
  `from_time` the stream replays history from that tick, then continues live.
- Every batch carries `catalog_version`. When a reload changes the topic set, the version bumps and clients
  call `Describe()` again.
- Keepalive pings detect a dead client within about 15 seconds.
- The protocol is read-only. Clients cannot send commands to the server.

## Client library

`TopicMonitor.Client` hides the streaming details:

- `PanelClient.ConnectTo(address)`, then `DescribeAsync()` and `SubscribeAsync(patterns, mode, fromTime)`.
- `GetState(name)` returns the current value, confidence, validity and timestamps; `GetHistory(name)` returns
  the time series.
- `SampleReceived` and `CatalogChanged` events. Marshal them to your UI thread yourself.
- Reconnects automatically on transport faults and on detected sequence gaps, re-describing the catalog and
  resuming from the last received tick.
- `Latency` records `recv − t_processed` per batch.

See [docs/writing-a-client.md](docs/writing-a-client.md) for a full walkthrough.

## Viewer

One shared time axis; each lane shows one topic with one renderer.

| Renderer  | Applies to                        | Default for       |
| --------- | --------------------------------- | ----------------- |
| `step`    | float, int                        | float, int        |
| `lines`   | vec (one line per component)      | vec               |
| `digital` | bool                              | bool              |
| `boxes`   | enum, string, float, int          | enum, string      |
| `swatch`  | vec with 3 components and `space` | (chosen per lane) |

Style resolution runs in order: the type default, then value rules from the layout (for example, the enum
value `blinking` gets a dashed border), then the quality overlay (low confidence is hatched, invalid is grey).
The lane list is defined by `layout.yaml`. Topics the server offers but the layout does not mention are added
automatically under "Unassigned" and listed in a banner.

```yaml
window: 10s
styles:
  red:      { fill: red }
  blinking: { border: dashed }
  "@invalid": { fill: "#888" }
templates:
  led:
    - { topic: "{p}.raw",   as: lines }
    - { topic: "{p}.color" }
    - { topic: "{p}.state" }
groups:
  - name: Sensors
    lanes:
      - { topic: temp, unit: C }
  - { name: Light 1, use: led, p: led.1 }
```

Controls:

- Mouse wheel scrolls the lane list. Ctrl+wheel or a trackpad pinch zooms. Dragging the timeline pans and pauses.
- Hover shows value, `t`, confidence and `evidence_since`. Click pins the value.
- Ctrl+click places cursors A and B. The toolbar shows the time of each and the Δt between them.
- Right-click a lane to hide it, change its renderer or style, or save unassigned lanes to `layout.yaml`.
  Hidden lanes come back from the "Hidden lanes" button.
- The status bar shows the connection, scenario version, sample rate, and latency p50 and max over 10 s.

## Run

```
dotnet build TopicMonitor.slnx
dotnet test
dotnet run --project src/TopicMonitor.Server
dotnet run --project src/TopicMonitor.Viewer.Wpf -- --server http://localhost:5279 --layout examples/layout.yaml
```

The server reads `examples/demo.scn` by default. Editing that file changes the data live, and editing
`examples/layout.yaml` changes the view. The viewer requires Windows (WPF); the server, client and tests run
anywhere .NET 8 does.

## Testing

- **Unit tests** (`tests/TopicMonitor.Tests.Unit`): deterministic, driven by `FakeTimeProvider`. They cover the
  scenario parser and expander, the bus (policies, history, invalidation), processors, the client state store,
  and the layout model.
- **Integration tests** (`tests/TopicMonitor.Tests.Integration`): start a real Kestrel server on a random
  localhost port and talk to it over gRPC. They cover the worked example, snapshots, sequence gaps, lossless
  reconnect after overflow, hot reload, and invalidation.
- **Latency suite** (`Category=Latency`): real-time load of 32 signals at 20 Hz with three clients, one of them
  deliberately slow. It asserts p99.9 and max of `recv − t_processed` under 100 ms for the fast clients. On the
  development machine the observed p99.9 was about 16 ms. It is excluded from a plain `dotnet test`; run it with:

```
dotnet test tests/TopicMonitor.Tests.Integration --filter "Category=Latency"
```

## Documentation

- [docs/writing-a-source.md](docs/writing-a-source.md): how to write a new producer against `TopicMonitor.Core`.
- [docs/writing-a-client.md](docs/writing-a-client.md): how to write a consumer against `TopicMonitor.Client`.

## License

MIT. See [LICENSE](LICENSE).
