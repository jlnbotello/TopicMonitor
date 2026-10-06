using Panel.Core;

namespace Panel.Sources.File;

/// <summary>
/// Drives scenario ticks on a shared <see cref="TimeProvider"/> via <see cref="TimeProvider.CreateTimer"/> (never
/// <see cref="System.Threading.Timer"/> or <see cref="Task.Delay(TimeSpan)"/>), so under a
/// <c>FakeTimeProvider</c> in tests, ticks only fire when the test explicitly advances the clock. Every tick
/// publishes a <see cref="Sample"/> with every one of the scenario's topics' current value (plan section 5:
/// "Every source tick produces a sample, even without changes"); the bus's own publish policies decide what
/// actually reaches history/subscribers. `@loop` needs no special handling here: the tick index just keeps
/// counting up forever and <see cref="ScenarioExpander.Evaluate"/> reduces it modulo the loop length internally.
/// </summary>
public sealed class ScenarioReplayer : IDisposable
{
    private readonly ITopicBus _bus;
    private readonly TimeProvider _timeProvider;
    private readonly object _lock = new();

    private ITimer? _timer;
    private LoadedScenario? _scenario;
    private long _tickIndex;
    private long _prevT;
    private ulong _seq;

    public ScenarioReplayer(ITopicBus bus, TimeProvider timeProvider)
    {
        _bus = bus;
        _timeProvider = timeProvider;
    }

    public LoadedScenario? Current => _scenario;

    /// <summary>Swaps in a new scenario and restarts replay from tick 0 (plan section 1: "replay restarts automatically when the file changes").</summary>
    public void SetScenario(LoadedScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        lock (_lock)
        {
            _timer?.Dispose();
            _scenario = scenario;
            _tickIndex = 0;
            _seq = 0;
            _prevT = _timeProvider.GetTimestamp();

            PublishTick(); // tick 0 fires immediately so subscribers see the initial state without waiting a full period

            var period = TimeSpan.FromMilliseconds(scenario.Expander.TickPeriodMs);
            _timer = _timeProvider.CreateTimer(_ => PublishTick(), null, period, period);
        }
    }

    private void PublishTick()
    {
        lock (_lock)
        {
            if (_scenario is null) return;

            var t = _timeProvider.GetTimestamp();
            var snapshot = _scenario.Expander.Evaluate(_tickIndex);

            var values = new List<TopicValue>(snapshot.Count);
            foreach (var (topicName, resolved) in snapshot)
            {
                if (!_scenario.Handles.TryGetValue(topicName, out var handle)) continue;
                values.Add(new TopicValue(handle, resolved.Value, resolved.Confidence, resolved.Validity, null));
            }

            var sample = new Sample(_scenario.SourceId, ++_seq, t, _prevT, t, values);
            _bus.Publish(sample);

            _prevT = t;
            _tickIndex++;
        }
    }

    /// <summary>Stops replay and marks the scenario's topics Invalid on the bus (hook for a server-driven shutdown path).</summary>
    public void Stop()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
            if (_scenario is not null)
                _bus.SetSourceInvalid(_scenario.SourceId);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }
}
