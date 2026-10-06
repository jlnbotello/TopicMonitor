using Panel.Core;

namespace Panel.Sources.File;

/// <summary>
/// Top-level composition of the `.scn` file source: parser + loader + replayer + an optional file watcher.
/// <see cref="TryLoad"/> is the pure, filesystem-free "parse + swap" path (unit-testable without a real OS file
/// event); <see cref="WatchFile"/> is the thin wrapper that drives it from real file-change notifications. On a
/// parse or validation error for a new file, whatever scenario was previously running keeps running untouched
/// (plan section 4: "the server keeps the last valid scenario running").
/// </summary>
public sealed class ScenarioFileSource : IDisposable
{
    private readonly ITopicBus _bus;
    private readonly int _noiseSeed;
    private readonly ScenarioReplayer _replayer;
    private ScenarioFileWatcher? _watcher;

    /// <summary>One entry per '@source' block in the currently running scenario file; null if nothing has
    /// loaded successfully yet.</summary>
    public IReadOnlyList<LoadedScenario>? CurrentScenarios => _replayer.CurrentScenarios;
    public IReadOnlyList<string> LastErrors { get; private set; } = Array.Empty<string>();

    public ScenarioFileSource(ITopicBus bus, TimeProvider timeProvider, int noiseSeed = 0)
    {
        _bus = bus;
        _noiseSeed = noiseSeed;
        _replayer = new ScenarioReplayer(bus, timeProvider);
    }

    /// <summary>
    /// Parses and validates <paramref name="scenarioText"/> and, if valid, swaps it in and restarts replay from
    /// t=0. Returns false and leaves the previously running scenario untouched on a parse or validation error.
    /// </summary>
    public bool TryLoad(string scenarioText, out IReadOnlyList<string> errors)
    {
        try
        {
            var doc = ScenarioParser.Parse(scenarioText);
            var loaded = ScenarioLoader.Load(doc, _bus, _noiseSeed);
            _replayer.SetScenario(loaded);
            errors = Array.Empty<string>();
            LastErrors = errors;
            return true;
        }
        catch (ScenarioParseException ex)
        {
            errors = new[] { ex.Message };
            LastErrors = errors;
            return false;
        }
        catch (ScenarioValidationException ex)
        {
            errors = new[] { ex.Message };
            LastErrors = errors;
            return false;
        }
    }

    /// <summary>Starts watching <paramref name="path"/>; on any change, re-reads and re-applies it via <see cref="TryLoad"/>.</summary>
    public void WatchFile(string path)
    {
        _watcher?.Dispose();
        _watcher = new ScenarioFileWatcher(path, OnFileChanged);
    }

    private void OnFileChanged(string path)
    {
        string text;
        try { text = System.IO.File.ReadAllText(path); }
        catch { return; } // transient IO error (e.g. editor mid-write); keep the currently running scenario
        TryLoad(text, out _);
    }

    /// <summary>Stops replay and marks the current scenario's topics Invalid on the bus (hook for a server shutdown path).</summary>
    public void Stop() => _replayer.Stop();

    public void Dispose()
    {
        _watcher?.Dispose();
        _replayer.Dispose();
    }
}
