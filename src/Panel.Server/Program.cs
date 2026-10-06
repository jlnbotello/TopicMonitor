using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using Panel.Core;
using Panel.Server;
using Panel.Server.Hosting;
using Panel.Server.Services;
using Panel.Sources.File;

var app = Program.CreateApp(args);
app.Run();

/// <summary>
/// Top-level statements above implicitly define this class's <c>Main</c>; the explicit partial
/// declaration below only adds <see cref="CreateApp"/> and makes the class <c>public</c> so
/// <c>Panel.Tests.Integration</c> can build and start the real composition in-process (real Kestrel, real
/// bus, real scenario load, real processors) without duplicating it.
/// </summary>
public partial class Program
{
    /// <summary>
    /// The server's composition root (plan sections 5 "Server internals", 6 "gRPC API v1", 7's
    /// server-side counterpart, and 9 "real Kestrel on a random localhost port"): one singleton
    /// <see cref="TopicBus"/>, the scenario file source feeding it, the three v1 processors consuming it,
    /// and the gRPC service exposing it.
    /// </summary>
    public static WebApplication CreateApp(string[] args) => CreateApp(args, configureServices: null);

    /// <summary>
    /// Testability hook (added for <c>Panel.Tests.Integration</c>, plan section 9): identical to
    /// <see cref="CreateApp(string[])"/> except <paramref name="configureServices"/>, when given, runs
    /// immediately before <c>builder.Build()</c> - i.e. after every registration above, so e.g.
    /// <c>services.AddSingleton&lt;TimeProvider&gt;(fakeTimeProvider)</c> is the *last* registration for
    /// that service type and therefore wins when anything later resolves <c>TimeProvider</c> via
    /// <c>GetRequiredService&lt;TimeProvider&gt;()</c> or constructor injection (verified empirically in
    /// the integration test suite). This lets a test drive the whole in-process server - bus, scenario
    /// replayer, processors - on a shared <c>FakeTimeProvider</c> it controls, without duplicating this
    /// composition root.
    /// </summary>
    public static WebApplication CreateApp(string[] args, Action<IServiceCollection>? configureServices)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Keepalive (plan section 6: "Keepalive pings detect dead clients within seconds"). HTTP/2 PING
        // frames are sent after 10s of connection inactivity; if no ack arrives within a further 5s,
        // Kestrel tears the connection down. Worst case a fully dead peer (process killed, cable pulled,
        // network partition) is detected in ~15s; a merely slow-but-alive client still acks well within
        // the 5s timeout, so this doesn't misfire under normal load or a brief GC pause on the client.
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ConfigureEndpointDefaults(lo => lo.Protocols = HttpProtocols.Http2);
            options.Limits.Http2.KeepAlivePingDelay = TimeSpan.FromSeconds(10);
            options.Limits.Http2.KeepAlivePingTimeout = TimeSpan.FromSeconds(5);
        });

        builder.Services.AddOptions<PanelOptions>().Bind(builder.Configuration.GetSection("Panel"));

        // Production always uses the real clock; TimeProvider is injected (rather than used as a static)
        // purely so it could be swapped later (e.g. in a future test harness), per the plan's "Test time"
        // decision (section 2).
        builder.Services.AddSingleton(TimeProvider.System);

        builder.Services.AddSingleton<ITopicBus>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<PanelOptions>>().Value;
            var timeProvider = sp.GetRequiredService<TimeProvider>();
            var retention = options.HistoryRetentionSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null;
            return new TopicBus(timeProvider, retention);
        });

        builder.Services.AddSingleton(sp =>
            new ScenarioFileSource(sp.GetRequiredService<ITopicBus>(), sp.GetRequiredService<TimeProvider>()));

        builder.Services.AddSingleton<ScenarioReadySignal>();
        builder.Services.AddHostedService<ScenarioLoaderHostedService>();
        builder.Services.AddHostedService<ProcessorsHostedService>();

        builder.Services.AddGrpc();

        configureServices?.Invoke(builder.Services);

        var app = builder.Build();

        app.MapGrpcService<TopicGrpcService>();
        app.MapGet("/", () => "Panel.Server is running. Use a gRPC client to connect to TopicService.");

        return app;
    }
}
