using Microsoft.Extensions.Options;
using Panel.Core;
using Panel.Processors;

namespace Panel.Server.Hosting;

/// <summary>
/// Starts the three v1 processors (plan section 5, "Processors v1") once the scenario file's initial load
/// attempt has completed (see <see cref="ScenarioReadySignal"/>), and keeps them running until the host
/// shuts down.
///
/// Each processor resolves its input glob against the bus catalog exactly once, synchronously, at the
/// start of its own <c>RunAsync</c> (before its first <c>await</c>) - so the color classifier must be
/// started, and therefore must have registered its `*.color` output topics, strictly before the blink
/// detector starts (its input glob is `led.*.color`). Starting the three as separate sequential
/// statements - each one calling <c>RunAsync</c> and capturing the returned task before the next
/// statement runs - makes that ordering an explicit, documented property of this method, rather than an
/// implicit side effect of C# evaluating one expression's arguments left-to-right.
///
/// A single processor throwing is logged (<see cref="ILogger"/>, Error level) and does not take down the
/// other two or the host; shutdown cancels all three cleanly via the normal <see cref="BackgroundService"/>
/// stopping token.
/// </summary>
public sealed class ProcessorsHostedService : BackgroundService
{
    private readonly ITopicBus _bus;
    private readonly TimeProvider _timeProvider;
    private readonly ScenarioReadySignal _ready;
    private readonly IOptions<PanelOptions> _options;
    private readonly ILogger<ProcessorsHostedService> _logger;

    public ProcessorsHostedService(
        ITopicBus bus,
        TimeProvider timeProvider,
        ScenarioReadySignal ready,
        IOptions<PanelOptions> options,
        ILogger<ProcessorsHostedService> logger)
    {
        _bus = bus;
        _timeProvider = timeProvider;
        _ready = ready;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _ready.Ready.WaitAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // host is shutting down before the scenario was ever loaded
        }

        var color = new ColorClassifierProcessor(_bus, new ColorClassifierConfig(
            Input: "led.*.raw",
            Space: "rgb",
            Components: new[] { "r", "g", "b" },
            References: _options.Value.ColorReferences));

        var blink = new BlinkDetectorProcessor(_bus, _timeProvider, new BlinkDetectorConfig(Input: "led.*.color"));

        var text = new TextStabilizerProcessor(_bus, new TextStabilizerConfig(Input: "display.line*.raw"));

        _logger.LogInformation(
            "Starting processors: {Color}, {Blink}, {Text}",
            ColorClassifierProcessor.ProcessorName,
            BlinkDetectorProcessor.ProcessorName,
            TextStabilizerProcessor.ProcessorName);

        // Sequential on purpose - see type doc above.
        var colorTask = RunGuarded(ColorClassifierProcessor.ProcessorName, color.RunAsync, stoppingToken);
        var blinkTask = RunGuarded(BlinkDetectorProcessor.ProcessorName, blink.RunAsync, stoppingToken);
        var textTask = RunGuarded(TextStabilizerProcessor.ProcessorName, text.RunAsync, stoppingToken);

        await Task.WhenAll(colorTask, blinkTask, textTask).ConfigureAwait(false);
    }

    private async Task RunGuarded(string name, Func<CancellationToken, Task> run, CancellationToken token)
    {
        try
        {
            await run(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Normal shutdown: the host's stopping token was triggered.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Processor {Processor} terminated unexpectedly", name);
        }
    }
}
