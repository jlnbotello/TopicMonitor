using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Panel.Client;
using Panel.Contracts;
using Panel.Sources.File;

namespace Panel.Tests.Integration;

/// <summary>
/// Shared integration test harness (plan section 9: "real Kestrel on a random localhost port"). Starts the
/// real <c>Panel.Server</c> composition root (<see cref="Program.CreateApp(string[], Action{IServiceCollection}?)"/>
/// - real Kestrel, real <c>TopicBus</c>, real scenario load/watch, real processors) against an inline
/// scenario written to a throwaway temp file, and hands back a thin API for connecting real
/// <see cref="PanelClient"/>s to it.
/// <para>
/// Two time modes:
/// <list type="bullet">
/// <item><c>useFakeTime: true</c> (the default) shares one <see cref="FakeTimeProvider"/>, exposed as
/// <see cref="Time"/>, across every time-driven component on the server (<c>TopicBus</c>, the scenario
/// replayer, the blink detector) via the <c>configureServices</c> hook added to <c>Program.CreateApp</c>
/// for exactly this purpose - verified empirically (see the hook's doc comment) to be the registration that
/// wins when anything later resolves <c>TimeProvider</c>. <see cref="AdvanceAsync"/> drives scenario replay
/// deterministically, matching plan section 9's worked example ("test mode: time driven by the test").</item>
/// <item><c>useFakeTime: false</c> leaves the server on the real <c>TimeProvider.System</c> (as in
/// production and in <c>ServerSmokeTests</c>); used by tests that drive a real <see cref="FileSystemWatcher"/>
/// (hot reload) where fake time buys nothing and a short real-time poll is simpler and matches the existing
/// smoke test's style.</item>
/// </list>
/// </para>
/// </summary>
public sealed class TestHost : IAsyncDisposable
{
    private readonly List<PanelClient> _clients = new();
    private readonly FakeTimeProvider? _fakeTime;
    private readonly long _baselineTimestamp;

    public WebApplication App { get; }
    public string Address { get; }
    public string ScenarioPath { get; }

    /// <summary>The shared fake clock driving the server, when started with <c>useFakeTime: true</c>.
    /// Throws if the host was started on real time instead.</summary>
    public FakeTimeProvider Time => _fakeTime ?? throw new InvalidOperationException(
        "This TestHost was started with useFakeTime: false; there is no FakeTimeProvider to advance.");

    private TestHost(WebApplication app, string address, string scenarioPath, FakeTimeProvider? fakeTime, long baselineTimestamp)
    {
        App = app;
        Address = address;
        ScenarioPath = scenarioPath;
        _fakeTime = fakeTime;
        _baselineTimestamp = baselineTimestamp;
    }

    /// <summary>
    /// Writes <paramref name="scenarioText"/> to a fresh temp `.scn` file, points a real server composition
    /// root at it (via <c>--Panel:ScenarioPath=</c>, so no DI override is needed just for the scenario
    /// path), starts it on an ephemeral localhost port, and returns once startup has completed (by which
    /// point - see <c>ProcessorsHostedService</c>'s doc comment - the initial scenario load attempt and the
    /// processors' one-time catalog resolution have both already run).
    /// </summary>
    public static async Task<TestHost> StartAsync(string scenarioText, bool useFakeTime = true)
    {
        var scenarioPath = Path.Combine(Path.GetTempPath(), $"panel-test-{Guid.NewGuid():N}.scn");
        await File.WriteAllTextAsync(scenarioPath, scenarioText).ConfigureAwait(false);

        var fakeTime = useFakeTime ? new FakeTimeProvider() : null;

        var args = new[]
        {
            "--urls=http://127.0.0.1:0",
            "--environment=Development",
            $"--Panel:ScenarioPath={scenarioPath}",
        };

        var app = Program.CreateApp(args, services =>
        {
            if (fakeTime is not null)
                services.AddSingleton<TimeProvider>(fakeTime);
        });

        await app.StartAsync().ConfigureAwait(false);

        var addressesFeature = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        var address = addressesFeature!.Addresses.First();

        // Baseline for TestHost.ToMs: the fake clock's value right as the host finished starting, which is
        // also (see ScenarioReplayer.SetScenario) the instant the scenario's own t=0 tick was published.
        var baseline = fakeTime?.GetTimestamp() ?? 0;

        return new TestHost(app, address, scenarioPath, fakeTime, baseline);
    }

    /// <summary>Connects a fresh <see cref="PanelClient"/> (owning its own channel) and starts a
    /// <see cref="DeliveryMode.Lossless"/> subscription for <paramref name="patterns"/>. The client is
    /// disposed automatically by <see cref="DisposeAsync"/>.</summary>
    public Task<PanelClient> ConnectAsync(params string[] patterns) =>
        ConnectAsync(patterns, DeliveryMode.Lossless, fromTime: null);

    public async Task<PanelClient> ConnectAsync(IReadOnlyList<string> patterns, DeliveryMode mode, long? fromTime = null)
    {
        var client = PanelClient.ConnectTo(Address);
        _clients.Add(client);

        await client.DescribeAsync().ConfigureAwait(false);
        await client.SubscribeAsync(patterns, mode, fromTime).ConfigureAwait(false);

        // PanelClient.SubscribeAsync starts its read loop as a fire-and-forget background Task and returns
        // immediately - it does not wait for the grpc call to actually reach the server and register with
        // the bus. For a FakeTimeProvider-driven test that immediately follows a connect with a burst
        // AdvanceAsync, that race is real: without this margin, the burst can run (and be published to
        // nobody) before the subscriber is registered. A real-time margin here (independent of the server's
        // own - possibly fake - clock) is the simplest fix and costs nothing when useFakeTime is false.
        await Task.Delay(200).ConfigureAwait(false);

        return client;
    }

    /// <summary>Builds a raw gRPC call against the real server, bypassing <see cref="PanelClient"/>
    /// entirely - for protocol-level assertions (e.g. observing the bare <see cref="RpcException"/> a
    /// Lossless overflow produces) that would otherwise be swallowed by <c>PanelClient</c>'s own automatic
    /// reconnect. Caller owns the returned channel and call.</summary>
    public (GrpcChannel Channel, TopicService.TopicServiceClient Client) ConnectRaw()
    {
        var channel = GrpcChannel.ForAddress(Address);
        return (channel, new TopicService.TopicServiceClient(channel));
    }

    /// <summary>
    /// Advances the shared <see cref="FakeTimeProvider"/> by <paramref name="delta"/>, firing every
    /// scenario-replay tick due along the way (see <c>ScenarioReplayer</c>: it schedules via
    /// <c>TimeProvider.CreateTimer</c>), then waits a short real-time margin for the resulting samples to
    /// flow through the (genuinely asynchronous) bus-channel -&gt; gRPC -&gt; network -&gt; client pipeline.
    /// </summary>
    /// <remarks>
    /// Empirically (see this suite's own failures while this helper was written), a single
    /// <c>FakeTimeProvider.Advance(bigDelta)</c> call moves the clock straight to the target instant before
    /// running any due callbacks, rather than simulating one instant per elapsed period - so every timer
    /// callback due within that one call observes the *same*, fully-advanced <c>GetTimestamp()</c>, not a
    /// stepped value matching its own due time. For a scenario where only *counts* matter (e.g. overflowing
    /// a bounded queue), that's irrelevant. For a scenario where individual samples' times are asserted on
    /// (the plan's own worked example: <c>("red", 550)</c>), it silently collapses every tick's timestamp
    /// in the window to the end of the window - this method instead advances in small <paramref name="step"/>
    /// increments (default 50us, far below any realistic source period) so each due tick gets its own
    /// <c>Advance</c> call and therefore its own correctly-stepped timestamp. The real-time settle margin is
    /// paid once at the end, not per step.
    /// </remarks>
    public async Task AdvanceAsync(TimeSpan delta, TimeSpan? settle = null, TimeSpan? step = null)
    {
        var stepSize = step ?? TimeSpan.FromMicroseconds(50);
        var remaining = delta;
        while (remaining > TimeSpan.Zero)
        {
            var thisStep = remaining < stepSize ? remaining : stepSize;
            Time.Advance(thisStep);
            remaining -= thisStep;
        }

        await Task.Delay(settle ?? TimeSpan.FromMilliseconds(300)).ConfigureAwait(false);
    }

    /// <summary>Converts a sample's QPC-style <c>T</c> (as carried on <see cref="HistoryPoint"/>/<see cref="TopicState"/>)
    /// to milliseconds relative to the scenario's own t=0, using the shared fake clock's frequency - lets a
    /// test reproduce the plan's worked example assertions (<c>("red", 550)</c>) directly.</summary>
    public double ToMs(long t) => (t - _baselineTimestamp) * 1000.0 / Time.TimestampFrequency;

    /// <summary>The real <see cref="ScenarioFileSource"/> singleton driving replay, for tests that need to
    /// trigger a server-side source stop directly (plan section 9's invalidation test) without tearing down
    /// the whole host.</summary>
    public ScenarioFileSource ScenarioSource => App.Services.GetRequiredService<ScenarioFileSource>();

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
        {
            try { await client.DisposeAsync().ConfigureAwait(false); }
            catch { /* best effort cleanup */ }
        }

        await App.StopAsync().ConfigureAwait(false);
        await App.DisposeAsync().ConfigureAwait(false);

        try { File.Delete(ScenarioPath); } catch { /* best effort cleanup */ }
    }
}

/// <summary>Small test-only conveniences for reading a <see cref="PanelClient"/>'s state/history by topic
/// name with enum values resolved to their names via the cached <see cref="PanelClient.Catalog"/>, instead
/// of raw wire indices - matching the plan's worked example's <c>client.History("led.1.color")</c> shape in
/// spirit, without requiring <c>Panel.Client</c> itself to grow a name-resolving history API it doesn't
/// otherwise need.</summary>
public static class PanelClientTestExtensions
{
    /// <summary>The topic's enum history as (name, ms-since-scenario-t0) pairs, in receipt order.</summary>
    public static IReadOnlyList<(string Name, double Ms)> EnumHistory(this PanelClient client, TestHost host, string topicName)
    {
        var info = client.Catalog?.Topics.FirstOrDefault(t => t.Name == topicName)
            ?? throw new InvalidOperationException($"Topic '{topicName}' is not in the client's cached catalog.");

        return client.GetHistory(topicName)
            .Select(p => (info.EnumValues[(int)p.Value.AsEnumIndex], host.ToMs(p.T)))
            .ToList();
    }

    /// <summary>The topic's current enum value name, resolved via the cached catalog, or null if unknown/no state yet.</summary>
    public static string? GetEnumState(this PanelClient client, string topicName)
    {
        var info = client.Catalog?.Topics.FirstOrDefault(t => t.Name == topicName);
        if (info is null) return null;

        var state = client.GetState(topicName);
        return state is null ? null : info.EnumValues[(int)state.Value.AsEnumIndex];
    }
}
