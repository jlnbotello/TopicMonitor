using Panel.Core;

namespace Panel.Sources.File;

/// <summary>
/// Drives scenario ticks on a shared <see cref="TimeProvider"/> via <see cref="TimeProvider.CreateTimer"/> (never
/// <see cref="System.Threading.Timer"/> or <see cref="Task.Delay(TimeSpan)"/>), so under a
/// <c>FakeTimeProvider</c> in tests, ticks only fire when the test explicitly advances the clock. Every tick
/// publishes a <see cref="Sample"/> with every one of that tick's source's topics' current value (plan section 5:
/// "Every source tick produces a sample, even without changes"); the bus's own publish policies decide what
/// actually reaches history/subscribers. `@loop` needs no special handling here: each source's tick index just
/// keeps counting up forever and <see cref="ScenarioExpander.Evaluate"/> reduces it modulo its own loop length
/// internally.
///
/// A scenario file may declare several '@source' blocks at different rates (e.g. a fast one for LEDs, a slower
/// one for display text); each gets its own independent timer, tick index and seq counter, started together by
/// <see cref="SetScenario"/> and torn down together by <see cref="Stop"/>/<see cref="Dispose"/>.
/// </summary>
public sealed class ScenarioReplayer : IDisposable
{
    private readonly ITopicBus _bus;
    private readonly TimeProvider _timeProvider;
    private readonly object _lock = new();

    private readonly List<SourceState> _sources = new();
    private IReadOnlyList<LoadedScenario>? _currentSnapshot;

    private sealed class SourceState
    {
        public required LoadedScenario Scenario;
        public ITimer? Timer;
        public long TickIndex;
        public long PrevT;
        public ulong Seq;
    }

    public ScenarioReplayer(ITopicBus bus, TimeProvider timeProvider)
    {
        _bus = bus;
        _timeProvider = timeProvider;
    }

    /// <summary>A stable snapshot reference: calling this twice without an intervening <see cref="SetScenario"/>
    /// or <see cref="Stop"/> returns the *same* list instance (rebuilt only when the running scenario
    /// actually changes), so callers can compare by reference to detect "nothing changed".</summary>
    public IReadOnlyList<LoadedScenario>? CurrentScenarios
    {
        get { lock (_lock) return _currentSnapshot; }
    }

    /// <summary>Swaps in a new scenario (one or more sources) and restarts replay from tick 0 for each (plan
    /// section 1: "replay restarts automatically when the file changes").</summary>
    public void SetScenario(IReadOnlyList<LoadedScenario> scenarios)
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        lock (_lock)
        {
            foreach (var s in _sources) s.Timer?.Dispose();
            _sources.Clear();

            foreach (var scenario in scenarios)
            {
                var state = new SourceState { Scenario = scenario, PrevT = _timeProvider.GetTimestamp() };
                _sources.Add(state);

                PublishTick(state); // tick 0 fires immediately so subscribers see the initial state without waiting a full period

                var period = TimeSpan.FromMilliseconds(scenario.Expander.TickPeriodMs);
                state.Timer = _timeProvider.CreateTimer(_ => PublishTickLocked(state), null, period, period);
            }

            _currentSnapshot = scenarios.Count == 0 ? null : scenarios.ToList();
        }
    }

    private void PublishTickLocked(SourceState state)
    {
        lock (_lock)
        {
            if (!_sources.Contains(state)) return; // superseded by a later SetScenario/Stop
            PublishTick(state);
        }
    }

    /// <summary>Must be called under <see cref="_lock"/>.</summary>
    private void PublishTick(SourceState state)
    {
        var t = _timeProvider.GetTimestamp();
        var snapshot = state.Scenario.Expander.Evaluate(state.TickIndex);

        var values = new List<TopicValue>(snapshot.Count);
        foreach (var (topicName, resolved) in snapshot)
        {
            if (!state.Scenario.Handles.TryGetValue(topicName, out var handle)) continue;
            values.Add(new TopicValue(handle, resolved.Value, resolved.Confidence, resolved.Validity, null));
        }

        var sample = new Sample(state.Scenario.SourceId, ++state.Seq, t, state.PrevT, t, values);
        _bus.Publish(sample);

        state.PrevT = t;
        state.TickIndex++;
    }

    /// <summary>Stops replay and marks every source's topics Invalid on the bus (hook for a server-driven shutdown path).</summary>
    public void Stop()
    {
        lock (_lock)
        {
            foreach (var s in _sources)
            {
                s.Timer?.Dispose();
                _bus.SetSourceInvalid(s.Scenario.SourceId);
            }
            _sources.Clear();
            _currentSnapshot = null;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var s in _sources) s.Timer?.Dispose();
            _sources.Clear();
            _currentSnapshot = null;
        }
    }
}
