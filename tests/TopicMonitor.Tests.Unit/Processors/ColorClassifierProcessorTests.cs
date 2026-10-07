using Microsoft.Extensions.Time.Testing;
using TopicMonitor.Core;
using TopicMonitor.Processors;
using Shouldly;

namespace TopicMonitor.Tests.Unit.Processors;

public class ColorClassifierProcessorTests
{
    private static readonly Dictionary<string, double[]> References = new()
    {
        ["off"] = new[] { 0.0, 0.0, 0.0 },
        ["red"] = new[] { 240.0, 20.0, 20.0 },
        ["green"] = new[] { 20.0, 230.0, 30.0 },
        ["yellow"] = new[] { 230.0, 200.0, 20.0 },
    };

    private static ColorClassifierConfig MakeConfig(int k = 2) => new(
        Input: "led.*.raw",
        Space: "rgb",
        Components: new[] { "r", "g", "b" },
        References: References,
        K: k);

    private static (TopicBus Bus, FakeTimeProvider Time, TopicHandle Raw) SetUp()
    {
        var time = new FakeTimeProvider();
        var bus = new TopicBus(time, TimeSpan.FromSeconds(30));
        var raw = bus.Register(new TopicDescriptor("led.1.raw", TopicType.Vec, "file", Components: new[] { "r", "g", "b" }));
        return (bus, time, raw);
    }

    private static void PublishRaw(TopicBus bus, FakeTimeProvider time, TopicHandle raw, double[] rgb, ulong seq)
    {
        var t = time.GetTimestamp();
        bus.Publish(new Sample(new SourceId("file"), seq, t, t, t,
            new[] { new TopicValue(raw, Value.OfVec(rgb), null, Validity.Valid, null) }));
    }

    private static void PublishInvalidRaw(TopicBus bus, FakeTimeProvider time, TopicHandle raw, ulong seq)
    {
        var t = time.GetTimestamp();
        bus.Publish(new Sample(new SourceId("file"), seq, t, t, t, new[] { TopicValue.Invalid(raw) }));
    }

    [Fact]
    public void Default_k_is_2_per_resolved_open_point()
    {
        new ColorClassifierConfig("led.*.raw", "rgb", new[] { "r", "g", "b" }, References).K.ShouldBe(2);
    }

    [Fact]
    public async Task Registers_derived_color_topic_from_raw_vec_topic()
    {
        using var lifetime = new TestLifetime();
        var (bus, _, _) = SetUp();
        var processor = new ColorClassifierProcessor(bus, MakeConfig());

        _ = processor.RunAsync(lifetime.Token);

        var handle = bus.TryGetHandle("led.1.color");
        handle.ShouldNotBeNull();
        var descriptor = bus.GetDescriptor(handle!.Value);
        descriptor.Type.ShouldBe(TopicType.Enum);
        descriptor.Kind.ShouldBe(TopicKind.Derived);
        descriptor.Producer.ShouldBe(ColorClassifierProcessor.ProcessorName);
        descriptor.DerivedFrom.ShouldBe(new[] { "led.1.raw" });
        descriptor.EnumValues.ShouldBe(References.Keys, ignoreOrder: true);
    }

    [Theory]
    [InlineData("off", 0.0, 0.0, 0.0)]
    [InlineData("red", 240.0, 20.0, 20.0)]
    [InlineData("green", 20.0, 230.0, 30.0)]
    [InlineData("yellow", 230.0, 200.0, 20.0)]
    public async Task Classifies_each_exact_reference_color(string expected, double r, double g, double b)
    {
        using var lifetime = new TestLifetime();
        var (bus, time, raw) = SetUp();
        var processor = new ColorClassifierProcessor(bus, MakeConfig());
        _ = processor.RunAsync(lifetime.Token);

        var reader = TestSupport.Subscribe(bus, "led.1.color", lifetime.Token);
        PublishRaw(bus, time, raw, new[] { r, g, b }, 1); // first-ever sample: bootstrap, adopted immediately

        var outputs = await reader.CollectAsync(1);
        outputs[0].Value.AsEnum.ShouldBe(expected);
        outputs[0].Validity.ShouldBe(Validity.Valid);
    }

    [Fact]
    public async Task Confidence_is_high_for_an_exact_match_and_near_zero_for_an_ambiguous_midpoint()
    {
        using var lifetime = new TestLifetime();
        var (bus, time, raw) = SetUp();
        var processor = new ColorClassifierProcessor(bus, MakeConfig());
        _ = processor.RunAsync(lifetime.Token);

        var reader = TestSupport.Subscribe(bus, "led.1.color", lifetime.Token);

        // Exact match to "red": d1 = 0, so confidence = (d2 - 0) / (0 + d2) = 1 regardless of d2.
        PublishRaw(bus, time, raw, new[] { 240.0, 20.0, 20.0 }, 1);
        var exact = await reader.CollectAsync(1);
        exact[0].Confidence.ShouldNotBeNull();
        exact[0].Confidence!.Value.ShouldBe(1.0f, 0.001f);

        // Exact midpoint between "off" (0,0,0) and "red" (240,20,20): equidistant -> d1 == d2 -> confidence 0.
        // This midpoint is itself closer to "off"/"red" than to "green"/"yellow", so the nearest pair is
        // unambiguous even though the margin between them is zero.
        time.Advance(TimeSpan.FromMilliseconds(50));
        PublishRaw(bus, time, raw, new[] { 120.0, 10.0, 10.0 }, 2);

        // The midpoint differs from the confirmed "red" so it only starts a 1/2 candidate run; feed it a
        // second time to confirm so we can read back the confidence that was computed on the triggering sample.
        time.Advance(TimeSpan.FromMilliseconds(50));
        PublishRaw(bus, time, raw, new[] { 120.0, 10.0, 10.0 }, 3);

        var ambiguous = await reader.CollectAsync(1);
        ambiguous[0].Confidence.ShouldNotBeNull();
        ambiguous[0].Confidence!.Value.ShouldBe(0.0f, 0.001f);
    }

    [Fact]
    public async Task New_color_requires_k_consecutive_samples_and_evidence_since_is_the_first_one()
    {
        using var lifetime = new TestLifetime();
        var (bus, time, raw) = SetUp();
        var processor = new ColorClassifierProcessor(bus, MakeConfig(k: 2));
        _ = processor.RunAsync(lifetime.Token);

        var reader = TestSupport.Subscribe(bus, "led.1.color", lifetime.Token);

        PublishRaw(bus, time, raw, References["off"], 1); // t0: bootstrap -> off, published immediately
        var t0 = time.GetTimestamp();

        time.Advance(TimeSpan.FromMilliseconds(50));
        var t1 = time.GetTimestamp();
        PublishRaw(bus, time, raw, References["red"], 2); // candidate red, 1/2

        time.Advance(TimeSpan.FromMilliseconds(50));
        var t2 = time.GetTimestamp();
        PublishRaw(bus, time, raw, References["red"], 3); // candidate red, 2/2 -> confirmed

        var samples = await reader.CollectSamplesAsync(2);

        samples[0].T.ShouldBe(t0);
        samples[0].Values[0].Value.AsEnum.ShouldBe("off");
        samples[0].Values[0].EvidenceSince.ShouldBe(t0);

        // The output sample's own T is the k-th (triggering/latest) supporting sample's time (t2), while
        // EvidenceSince is the first of the two supporting samples (t1) - 
        samples[1].T.ShouldBe(t2);
        samples[1].Values[0].Value.AsEnum.ShouldBe("red");
        samples[1].Values[0].EvidenceSince.ShouldBe(t1);
    }

    [Fact]
    public async Task Single_sample_glitch_does_not_flip_the_confirmed_color()
    {
        using var lifetime = new TestLifetime();
        var (bus, time, raw) = SetUp();
        var processor = new ColorClassifierProcessor(bus, MakeConfig(k: 2));
        _ = processor.RunAsync(lifetime.Token);

        var reader = TestSupport.Subscribe(bus, "led.1.color", lifetime.Token);

        PublishRaw(bus, time, raw, References["off"], 1); // bootstrap -> off

        time.Advance(TimeSpan.FromMilliseconds(50));
        PublishRaw(bus, time, raw, References["red"], 2); // candidate red, 1/2

        time.Advance(TimeSpan.FromMilliseconds(50));
        PublishRaw(bus, time, raw, References["off"], 3); // glitch: back to off -> candidate cancelled, no publish (unchanged)

        time.Advance(TimeSpan.FromMilliseconds(50));
        var tRestart = time.GetTimestamp();
        PublishRaw(bus, time, raw, References["red"], 4); // candidate red restarts, 1/2

        time.Advance(TimeSpan.FromMilliseconds(50));
        PublishRaw(bus, time, raw, References["red"], 5); // candidate red, 2/2 -> confirmed

        // Exactly two outputs ever reach the bus: the bootstrap "off", and the eventual confirmed "red".
        // The single-sample glitch at t=100ms produced no output and did not count toward the debounce.
        var outputs = await reader.CollectAsync(2);
        outputs[0].Value.AsEnum.ShouldBe("off");
        outputs[1].Value.AsEnum.ShouldBe("red");
        outputs[1].EvidenceSince.ShouldBe(tRestart); // evidence starts at the restarted run, not the earlier glitch
    }

    [Fact]
    public async Task Invalid_raw_sample_propagates_as_invalid_and_resets_debounce()
    {
        using var lifetime = new TestLifetime();
        var (bus, time, raw) = SetUp();
        var processor = new ColorClassifierProcessor(bus, MakeConfig(k: 2));
        _ = processor.RunAsync(lifetime.Token);

        var reader = TestSupport.Subscribe(bus, "led.1.color", lifetime.Token);

        PublishRaw(bus, time, raw, References["red"], 1); // bootstrap -> red

        time.Advance(TimeSpan.FromMilliseconds(50));
        PublishInvalidRaw(bus, time, raw, 2); // invalid -> propagate Invalid, reset debounce

        time.Advance(TimeSpan.FromMilliseconds(50));
        PublishRaw(bus, time, raw, References["green"], 3); // first sample after the gap: re-bootstraps immediately

        var outputs = await reader.CollectAsync(3);

        outputs[0].Value.AsEnum.ShouldBe("red");
        outputs[0].Validity.ShouldBe(Validity.Valid);

        outputs[1].Validity.ShouldBe(Validity.Invalid);

        outputs[2].Value.AsEnum.ShouldBe("green");
        outputs[2].Validity.ShouldBe(Validity.Valid);
    }
}
