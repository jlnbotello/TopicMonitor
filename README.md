# TopicMonitor

TopicMonitor is a generic, timestamped topic bus with a gRPC server, a .NET client library, and a WPF
timeline viewer — built to watch many fast-changing values (sensor readings, LED states, display text,
anything with a name and a value over time) side by side on one shared, scrollable, zoomable time axis.
A replayable scenario file drives the data for development and testing, so the whole pipeline — bus,
derived-value processors, gRPC transport, client reconnect logic, and the viewer itself — runs and is
testable without any real hardware attached.

![TopicMonitor viewer showing LED color/state/frequency lanes alongside display text, with two time cursors placed and a hover tooltip open](docs/images/image_01.png)

See [docs/plan.md](docs/plan.md) for the full design and phased implementation plan.

## Solution layout

- `src/Panel.Contracts` — `.proto` definitions and generated gRPC/C# types
- `src/Panel.Core` — topic bus, descriptors, samples, publish policies, history
- `src/Panel.Processors` — color classifier, blink detector, text stabilizer
- `src/Panel.Sources.File` — scenario (`.scn`) parser, expander, replayer, file watcher
- `src/Panel.Server` — Kestrel host, gRPC services, configuration
- `src/Panel.Client` — catalog, subscription, state store, history, reconnect
- `src/Panel.Viewer.Layout` — YAML layout model, template expansion, style resolution
- `src/Panel.Viewer.Wpf` — timeline control, lane renderers, interaction
- `tests/Panel.Tests.Unit` — unit tests (deterministic, no GUI)
- `tests/Panel.Tests.Integration` — local integration tests over real gRPC

## Build and test

```
dotnet build
dotnet test
```

## Run

```
dotnet run --project src/Panel.Server
dotnet run --project src/Panel.Viewer.Wpf -- --server http://localhost:5279 --layout examples/layout.yaml
```

Edit `examples/demo.scn` to change the data and `examples/layout.yaml` to change the view; both reload live.
Viewer: wheel zooms, drag pans (pauses), click pins a value, Ctrl+click places cursors A/B, right-click a lane to change its renderer or save unassigned lanes to the layout file.
