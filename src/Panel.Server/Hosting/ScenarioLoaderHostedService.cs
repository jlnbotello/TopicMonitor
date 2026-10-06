using Microsoft.Extensions.Options;
using Panel.Sources.File;

namespace Panel.Server.Hosting;

/// <summary>
/// Loads the configured `.scn` scenario file once at startup and starts watching it for live edits (plan
/// section 4: "watched, auto-restart"). Runs as an <see cref="IHostedService"/> so it participates in the
/// host's ordered startup/shutdown: <see cref="StopAsync"/> marks the scenario's topics Invalid (plan
/// section 5: "a source that stops sets all its topics to Invalid").
///
/// Signals <see cref="ScenarioReadySignal"/> once the initial load attempt has completed, so
/// <see cref="ProcessorsHostedService"/> never races the first load. Judgment call: the signal fires after
/// the *attempt*, not only on success. If the scenario file is missing or invalid at startup, that's a
/// clearly logged operator error, but it should not wedge the processors forever - with zero raw topics
/// registered they simply become no-ops until a later file edit (picked up by the watcher) succeeds.
/// </summary>
public sealed class ScenarioLoaderHostedService : IHostedService
{
    private readonly ScenarioFileSource _source;
    private readonly ScenarioReadySignal _ready;
    private readonly IOptions<PanelOptions> _options;
    private readonly IHostEnvironment _env;
    private readonly ILogger<ScenarioLoaderHostedService> _logger;

    public ScenarioLoaderHostedService(
        ScenarioFileSource source,
        ScenarioReadySignal ready,
        IOptions<PanelOptions> options,
        IHostEnvironment env,
        ILogger<ScenarioLoaderHostedService> logger)
    {
        _source = source;
        _ready = ready;
        _options = options;
        _env = env;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var path = ScenarioPathResolver.Resolve(_env.ContentRootPath, _options.Value.ScenarioPath);
        _logger.LogInformation("Loading scenario file from {Path}", path);

        if (System.IO.File.Exists(path))
        {
            try
            {
                var text = System.IO.File.ReadAllText(path);
                if (_source.TryLoad(text, out var errors))
                {
                    _logger.LogInformation("Scenario loaded successfully from {Path}", path);
                }
                else
                {
                    foreach (var error in errors)
                        _logger.LogError("Scenario load error in {Path}: {Error}", path, error);
                }
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Failed to read scenario file {Path}", path);
            }
        }
        else
        {
            _logger.LogError(
                "Scenario file not found at {Path}; starting with an empty catalog until a valid file appears there",
                path);
        }

        // Watch regardless of whether the initial load succeeded: FileSystemWatcher only needs the
        // directory to exist, so a scenario file created or fixed later is picked up automatically.
        _source.WatchFile(path);
        _ready.MarkReady();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _source.Stop();
        return Task.CompletedTask;
    }
}
