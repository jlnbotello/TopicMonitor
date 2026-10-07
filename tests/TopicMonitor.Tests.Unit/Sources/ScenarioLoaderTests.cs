using Microsoft.Extensions.Time.Testing;
using TopicMonitor.Core;
using TopicMonitor.Sources.File;
using Shouldly;

namespace TopicMonitor.Tests.Unit.Sources;

public class ScenarioLoaderTests
{
    private static TopicBus CreateBus() => new(new FakeTimeProvider());

    [Fact]
    public void Registers_declared_topics_on_the_bus_with_the_source_as_producer()
    {
        var bus = CreateBus();
        var doc = ScenarioParser.Parse("""
            @source cam rate=20
            @topic led.1.raw vec[r,g,b]
            @topic display.line1.raw string
            0 led.1.raw=(0,0,0) display.line1.raw="BOOT"
            """);

        var loaded = ScenarioLoader.Load(doc, bus).Single();

        loaded.SourceId.ShouldBe(new SourceId("cam"));
        loaded.Handles.Count.ShouldBe(2);

        var ledDescriptor = bus.GetDescriptor(loaded.Handles["led.1.raw"]);
        ledDescriptor.Producer.ShouldBe("cam");
        ledDescriptor.Type.ShouldBe(TopicType.Vec);
        ledDescriptor.Components.ShouldBe(new[] { "r", "g", "b" });

        var lineDescriptor = bus.GetDescriptor(loaded.Handles["display.line1.raw"]);
        lineDescriptor.Type.ShouldBe(TopicType.String);
    }

    [Fact]
    public void Topic_already_owned_by_another_producer_is_a_clear_validation_error()
    {
        var bus = CreateBus();
        bus.Register(new TopicDescriptor("led.1.raw", TopicType.Vec, "some.processor", Components: new[] { "r", "g", "b" }));

        var doc = ScenarioParser.Parse("@source cam rate=20\n@topic led.1.raw vec[r,g,b]\n0 led.1.raw=(0,0,0)\n");

        var ex = Should.Throw<ScenarioValidationException>(() => ScenarioLoader.Load(doc, bus));
        ex.Message.ShouldContain("led.1.raw");
        ex.Message.ShouldContain("some.processor");
    }

    [Fact]
    public void Reloading_the_same_source_with_unchanged_topics_reuses_the_existing_handle()
    {
        var bus = CreateBus();
        var text = "@source cam rate=20\n@topic led.1.raw vec[r,g,b]\n0 led.1.raw=(0,0,0)\n";

        var first = ScenarioLoader.Load(ScenarioParser.Parse(text), bus).Single();
        var second = ScenarioLoader.Load(ScenarioParser.Parse(text), bus).Single();

        second.Handles["led.1.raw"].ShouldBe(first.Handles["led.1.raw"]);
    }

    [Fact]
    public void Reloading_with_an_incompatible_type_change_for_the_same_topic_is_a_validation_error()
    {
        var bus = CreateBus();
        ScenarioLoader.Load(ScenarioParser.Parse("@source cam rate=20\n@topic led.1.raw vec[r,g,b]\n0 led.1.raw=(0,0,0)\n"), bus);

        var changed = ScenarioParser.Parse("@source cam rate=20\n@topic led.1.raw float\n0 led.1.raw=1\n");

        Should.Throw<ScenarioValidationException>(() => ScenarioLoader.Load(changed, bus));
    }

    [Fact]
    public void Missing_source_directive_is_a_validation_error()
    {
        var doc = ScenarioParser.Parse("@topic t float\n0 t=1\n");
        Should.Throw<ScenarioValidationException>(() => ScenarioLoader.Load(doc, CreateBus()));
    }

    [Fact]
    public void Topic_declared_before_any_source_is_a_validation_error()
    {
        var doc = ScenarioParser.Parse("@topic orphan float\n@source cam rate=20\n@topic t float\n0 orphan=1 t=2\n");
        var ex = Should.Throw<ScenarioValidationException>(() => ScenarioLoader.Load(doc, CreateBus()));
        ex.Message.ShouldContain("orphan");
    }

    [Fact]
    public void Multiple_sources_each_produce_their_own_loaded_scenario_with_only_their_own_topics()
    {
        var bus = CreateBus();
        var doc = ScenarioParser.Parse("""
            @source cam rate=20
            @topic led.1.raw vec[r,g,b]
            @source ocr rate=2
            @topic display.line1.raw string
            0 led.1.raw=(0,0,0) display.line1.raw="BOOT"
            """);

        var scenarios = ScenarioLoader.Load(doc, bus);

        scenarios.Count.ShouldBe(2);
        var cam = scenarios.Single(s => s.SourceId == new SourceId("cam"));
        var ocr = scenarios.Single(s => s.SourceId == new SourceId("ocr"));

        cam.Handles.Keys.ShouldBe(new[] { "led.1.raw" });
        ocr.Handles.Keys.ShouldBe(new[] { "display.line1.raw" });
        cam.Expander.Rate.ShouldBe(20);
        ocr.Expander.Rate.ShouldBe(2);

        bus.GetDescriptor(cam.Handles["led.1.raw"]).Producer.ShouldBe("cam");
        bus.GetDescriptor(ocr.Handles["display.line1.raw"]).Producer.ShouldBe("ocr");
    }
}

public class ScenarioFileSourceTests
{
    private static ScenarioFileSource CreateSource(out TopicBus bus, out FakeTimeProvider time)
    {
        time = new FakeTimeProvider();
        bus = new TopicBus(time);
        return new ScenarioFileSource(bus, time);
    }

    [Fact]
    public void TryLoad_swaps_in_a_valid_scenario()
    {
        var source = CreateSource(out _, out _);

        var ok = source.TryLoad("@source cam rate=20\n@topic t float\n0 t=1\n", out var errors);

        ok.ShouldBeTrue();
        errors.ShouldBeEmpty();
        source.CurrentScenarios.ShouldNotBeNull();
        source.CurrentScenarios!.Single().SourceId.ShouldBe(new SourceId("cam"));
    }

    [Fact]
    public void TryLoad_reports_parse_errors_without_throwing()
    {
        var source = CreateSource(out _, out _);

        var ok = source.TryLoad("@bogus\n", out var errors);

        ok.ShouldBeFalse();
        errors.ShouldNotBeEmpty();
        source.CurrentScenarios.ShouldBeNull();
    }

    [Fact]
    public void An_invalid_replacement_keeps_the_previously_running_scenario()
    {
        var source = CreateSource(out _, out _);
        source.TryLoad("@source cam rate=20\n@topic t float\n0 t=1\n", out _);
        var runningBefore = source.CurrentScenarios;

        var ok = source.TryLoad("@source cam rate=20\n@topic t float\n0 t=\"not a float\"\n", out var errors);

        ok.ShouldBeFalse();
        errors.ShouldNotBeEmpty();
        source.CurrentScenarios.ShouldBeSameAs(runningBefore);
    }

    [Fact]
    public async Task A_valid_replacement_swaps_in_and_restarts_replay_from_tick_zero()
    {
        var source = CreateSource(out var bus, out _);
        source.TryLoad("@source cam rate=20\n@topic t float\n0 t=1\n", out _);

        using var cts = new CancellationTokenSource();
        var enumerator = bus.Subscribe(TopicFilter.All, null, DeliveryMode.Lossless, cts.Token).GetAsyncEnumerator(cts.Token);

        // Subscribe's async-iterator body (including subscriber registration) only runs once MoveNextAsync is
        // first called - calling it here (without awaiting yet) registers the subscriber synchronously, so the
        // tick-0 publish below (which also happens synchronously, inside TryLoad) isn't missed.
        var moveNext = enumerator.MoveNextAsync();

        // The swap immediately (synchronously) publishes tick 0 of the new scenario.
        var ok = source.TryLoad("@source cam rate=20\n@topic t float\n0 t=42\n", out _);
        ok.ShouldBeTrue();

        (await moveNext).ShouldBeTrue();
        var handle = source.CurrentScenarios!.Single().Handles["t"];
        var topicValue = enumerator.Current.Values.Single(v => v.Topic == handle);
        topicValue.Value.AsFloat.ShouldBe(42.0);
    }
}
