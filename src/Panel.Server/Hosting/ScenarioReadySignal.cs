namespace Panel.Server.Hosting;

/// <summary>
/// Signals once the scenario file's initial load attempt has completed, so that
/// <see cref="ProcessorsHostedService"/> - whose processors each resolve their input glob against the bus
/// catalog exactly once, at startup (see Panel.Processors) - never races
/// <see cref="ScenarioLoaderHostedService"/>'s first load of `examples/demo.scn` (or whatever scenario is
/// configured).
/// </summary>
public sealed class ScenarioReadySignal
{
    private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Ready => _tcs.Task;

    public void MarkReady() => _tcs.TrySetResult();
}
