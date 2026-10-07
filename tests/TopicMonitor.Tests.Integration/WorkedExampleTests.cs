using TopicMonitor.Contracts;
using Shouldly;

namespace TopicMonitor.Tests.Integration;

/// <summary>
/// Reproduces own worked example verbatim (scenario text and final assertions), end to
/// end: file -> raw `led.1.raw` samples -> color classifier -> gRPC -> real <c>PanelClient</c>. This is the
/// plan's explicit statement of "color classifier k=2 means one sample of delay" as an observable
/// end-to-end behavior (not just something the processor's own unit tests cover in isolation).
/// </summary>
public class WorkedExampleTests
{
    [Fact]
    public async Task Red_pulse_is_classified_with_one_sample_delay()
    {
        await using var h = await TestHost.StartAsync("""
            @source cam rate=20
            @topic led.1.raw vec[r,g,b]
            0    led.1.raw=(0,0,0)
            500  led.1.raw=(240,20,20)
            700  led.1.raw=(0,0,0)
            """); // test mode: time driven by the test (FakeTimeProvider, the default)

        // fromTime: long.MinValue replays full history from t=0, not just live-from-now: the color
        // classifier (TopicMonitor.Processors) subscribes to its own raw input the same way (see
        // ColorClassifierProcessor.RunAsync's doc comment), and bootstraps its "off" classification from
        // the scenario's t=0 tick during server startup -- before this test client ever connects. A
        // live-only subscribe (fromTime: null, this harness's default for other tests) would miss that
        // already-published sample entirely, the same way any other interested subscriber would if it
        // connected after the fact without asking for replay.
        var client = await h.ConnectAsync(new[] { "led.1.color" }, DeliveryMode.Lossless, fromTime: long.MinValue);
        await h.AdvanceAsync(TimeSpan.FromSeconds(1));

        client.EnumHistory(h, "led.1.color").ShouldBe(new[]
        {
            ("off", 0.0),
            ("red", 550.0),
            ("off", 750.0),
        });
    }
}
