using Microsoft.Extensions.Time.Testing;
using Panel.Core;
using Panel.Sources.File;
using Shouldly;

namespace Panel.Tests.Unit.Sources;

/// <summary>
/// End-to-end: a real <see cref="TopicBus"/> fed by <see cref="ScenarioFileSource"/>, driven tick-by-tick on a
/// <see cref="FakeTimeProvider"/>, asserted against the `demo.scn` worked example from plan section 4 (noise
/// omitted here so every value is exactly predictable).
/// </summary>
public class ScenarioReplayerEndToEndTests
{
    private const string DemoScenario = """
        @source cam rate=20
        @topic led.1.raw vec[r,g,b]
        @topic led.2.raw vec[r,g,b]
        @topic led.3.raw vec[r,g,b]
        @topic display.line1.raw string
        @topic display.line2.raw string

        0        led.1.raw=(0,0,0)  led.2.raw=(0,0,0)  led.3.raw=(0,0,0)  display.line1.raw="BOOT"  display.line2.raw=""
        500      led.1.raw=(240,20,20)
        +100     led.1.raw=(0,0,0)
        1200     led.2.raw=(20,230,30)  display.line1.raw="READY"  display.line2.raw="v1.2"
        1500     led.1.raw=blink((240,20,20),(0,0,0),5Hz)
        2000     led.3.raw=(230,200,20)
        """;

    [Fact]
    public async Task Published_samples_match_the_demo_scn_worked_example_at_the_right_tick_timestamps()
    {
        var time = new FakeTimeProvider();
        var bus = new TopicBus(time, TimeSpan.FromMinutes(1));
        var source = new ScenarioFileSource(bus, time);

        using var cts = new CancellationTokenSource();
        var enumerator = bus.Subscribe(TopicFilter.All, null, DeliveryMode.Lossless, cts.Token).GetAsyncEnumerator(cts.Token);

        // Subscribe's async-iterator body (including subscriber registration) only runs once MoveNextAsync is
        // first called - calling it here (without awaiting yet) registers the subscriber synchronously, so the
        // tick-0 publish below (which also happens synchronously, inside TryLoad) isn't missed.
        var moveNext = enumerator.MoveNextAsync();

        var t0 = time.GetTimestamp();
        var ok = source.TryLoad(DemoScenario, out var errors);
        ok.ShouldBeTrue(string.Join("; ", errors));

        Value Of(Sample sample, string topicName) =>
            sample.Values.Single(v => v.Topic == source.CurrentScenarios!.Single().Handles[topicName]).Value;

        var currentTick = 0;
        Sample? lastSample = null;

        async Task<Sample> AdvanceToTickAsync(int targetTick)
        {
            while (currentTick < targetTick)
            {
                time.Advance(TimeSpan.FromMilliseconds(50)); // one source tick at rate=20
                (await enumerator.MoveNextAsync()).ShouldBeTrue();
                lastSample = enumerator.Current;
                currentTick++;
            }
            return lastSample!;
        }

        // Tick 0 (t=0ms): the initial boot state, published synchronously when TryLoad swaps the scenario in.
        (await moveNext).ShouldBeTrue();
        var tick0 = enumerator.Current;
        currentTick = 0;
        lastSample = tick0;
        tick0.T.ShouldBe(t0);
        tick0.TPrev.ShouldBe(t0);
        time.GetElapsedTime(t0, tick0.T).ShouldBe(TimeSpan.Zero);
        Of(tick0, "led.1.raw").AsVec.ShouldBe(new[] { 0.0, 0.0, 0.0 });
        Of(tick0, "led.2.raw").AsVec.ShouldBe(new[] { 0.0, 0.0, 0.0 });
        Of(tick0, "led.3.raw").AsVec.ShouldBe(new[] { 0.0, 0.0, 0.0 });
        Of(tick0, "display.line1.raw").AsString.ShouldBe("BOOT");
        Of(tick0, "display.line2.raw").AsString.ShouldBe("");

        // Tick 10 (t=500ms): led.1 turns red.
        var tick10 = await AdvanceToTickAsync(10);
        time.GetElapsedTime(t0, tick10.T).ShouldBe(TimeSpan.FromMilliseconds(500));
        Of(tick10, "led.1.raw").AsVec.ShouldBe(new[] { 240.0, 20.0, 20.0 });

        // Tick 12 (t=600ms, "+100" relative to the 500ms line): led.1 back off.
        var tick12 = await AdvanceToTickAsync(12);
        time.GetElapsedTime(t0, tick12.T).ShouldBe(TimeSpan.FromMilliseconds(600));
        Of(tick12, "led.1.raw").AsVec.ShouldBe(new[] { 0.0, 0.0, 0.0 });

        // Tick 24 (t=1200ms): led.2 green, display shows READY / v1.2.
        var tick24 = await AdvanceToTickAsync(24);
        time.GetElapsedTime(t0, tick24.T).ShouldBe(TimeSpan.FromMilliseconds(1200));
        Of(tick24, "led.2.raw").AsVec.ShouldBe(new[] { 20.0, 230.0, 30.0 });
        Of(tick24, "display.line1.raw").AsString.ShouldBe("READY");
        Of(tick24, "display.line2.raw").AsString.ShouldBe("v1.2");

        // Tick 30 (t=1500ms): led.1 starts blinking red/off at 5Hz - elapsed 0 at the start of the "on" phase.
        var tick30 = await AdvanceToTickAsync(30);
        time.GetElapsedTime(t0, tick30.T).ShouldBe(TimeSpan.FromMilliseconds(1500));
        Of(tick30, "led.1.raw").AsVec.ShouldBe(new[] { 240.0, 20.0, 20.0 });

        // Tick 32 (t=1600ms): 100ms into the 200ms blink period -> the "off" half.
        var tick32 = await AdvanceToTickAsync(32);
        Of(tick32, "led.1.raw").AsVec.ShouldBe(new[] { 0.0, 0.0, 0.0 });

        // Tick 40 (t=2000ms): led.3 turns yellow; led.1's blink is still running independently.
        var tick40 = await AdvanceToTickAsync(40);
        time.GetElapsedTime(t0, tick40.T).ShouldBe(TimeSpan.FromMilliseconds(2000));
        Of(tick40, "led.3.raw").AsVec.ShouldBe(new[] { 230.0, 200.0, 20.0 });
        Of(tick40, "led.1.raw").AsVec.ShouldBe(new[] { 0.0, 0.0, 0.0 }); // (2000-1500)=500ms elapsed, phase 100 -> off half

        // Every sample carries every declared topic, even unchanged ones (plan section 5: "every source tick
        // produces a sample, even without changes" - policies on the bus, not the source, decide what's dropped).
        tick40.Values.Count.ShouldBe(5);
    }

    [Fact]
    public async Task Two_sources_at_different_rates_tick_independently_and_loop_together()
    {
        // "cam" samples 10x faster than "ocr" (a stand-in for a slow text reader vs. a fast LED camera) -
        // the point of multi-source files: a display line shouldn't be sampled as if it were an LED.
        const string scenario = """
            @source cam rate=20
            @topic led.1.raw vec[r,g,b]
            @source ocr rate=2
            @topic display.line1.raw string

            0    led.1.raw=(0,0,0)       display.line1.raw="BOOT"
            500  led.1.raw=(240,20,20)   display.line1.raw="READY"
            @loop
            """;

        var time = new FakeTimeProvider();
        var bus = new TopicBus(time, TimeSpan.FromMinutes(1));
        var source = new ScenarioFileSource(bus, time);

        using var cts = new CancellationTokenSource();
        var enumerator = bus.Subscribe(TopicFilter.All, null, DeliveryMode.Lossless, cts.Token).GetAsyncEnumerator(cts.Token);
        var moveNext = enumerator.MoveNextAsync();

        var ok = source.TryLoad(scenario, out var errors);
        ok.ShouldBeTrue(string.Join("; ", errors));
        (await moveNext).ShouldBeTrue(); // cam's tick 0
        (await enumerator.MoveNextAsync()).ShouldBeTrue(); // ocr's tick 0

        // FakeTimeProvider timer callbacks run synchronously inside Advance, so by the time all 20 steps
        // below return, every due tick (cam every 50ms, ocr every 500ms) has already been published into
        // the bounded channel; draining with a single, non-overlapping MoveNextAsync loop afterward avoids
        // ever having two outstanding MoveNextAsync calls on the same enumerator at once (which corrupts
        // its state -- calling it again before the previous call completes is not supported).
        for (var i = 0; i < 20; i++)
            time.Advance(TimeSpan.FromMilliseconds(50));

        var camCount = 0;
        var ocrCount = 0;
        for (var i = 0; i < 22; i++) // cam: 1000ms/50ms = 20 more ticks; ocr: 1000ms/500ms = 2 more ticks
        {
            (await enumerator.MoveNextAsync()).ShouldBeTrue();
            if (enumerator.Current.Source == new SourceId("cam")) camCount++;
            else if (enumerator.Current.Source == new SourceId("ocr")) ocrCount++;
        }

        camCount.ShouldBe(20);
        ocrCount.ShouldBe(2);

        // Both sources' own display of "READY"/color-change landed at the same wall-clock t=500ms, each in
        // its own tick units (cam's 10th tick, ocr's 1st tick after tick 0) - @loop keeps them in lockstep
        // at the file's shared last-event time, not at some per-source tick count.
        var camScenario = source.CurrentScenarios!.Single(s => s.SourceId == new SourceId("cam"));
        var ocrScenario = source.CurrentScenarios!.Single(s => s.SourceId == new SourceId("ocr"));
        camScenario.Expander.Evaluate(10)["led.1.raw"].Value.AsVec.ShouldBe(new[] { 240.0, 20.0, 20.0 });
        ocrScenario.Expander.Evaluate(1)["display.line1.raw"].Value.AsString.ShouldBe("READY");
    }
}
