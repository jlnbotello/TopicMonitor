# TopicMonitor

A generic, timestamped topic server and WPF timeline viewer for monitoring panel state (LEDs, displays, etc.) from a replayable scenario file.

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
