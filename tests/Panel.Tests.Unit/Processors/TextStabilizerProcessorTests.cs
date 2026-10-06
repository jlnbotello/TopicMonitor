using Microsoft.Extensions.Time.Testing;
using Panel.Core;
using Panel.Processors;
using Shouldly;

namespace Panel.Tests.Unit.Processors;

public class TextStabilizerProcessorTests
{
    private static (TopicBus Bus, FakeTimeProvider Time, TopicHandle Raw) SetUp()
    {
        var time = new FakeTimeProvider();
        var bus = new TopicBus(time, TimeSpan.FromSeconds(30));
        var raw = bus.Register(new TopicDescriptor("display.line1.raw", TopicType.String, "file"));
        return (bus, time, raw);
    }

    private static void PublishRaw(TopicBus bus, FakeTimeProvider time, TopicHandle raw, string text, ulong seq)
    {
        var t = time.GetTimestamp();
        bus.Publish(new Sample(new SourceId("file"), seq, t, t, t,
            new[] { new TopicValue(raw, Value.OfString(text), null, Validity.Valid, null) }));
    }

    private static void PublishInvalidRaw(TopicBus bus, FakeTimeProvider time, TopicHandle raw, ulong seq)
    {
        var t = time.GetTimestamp();
        bus.Publish(new Sample(new SourceId("file"), seq, t, t, t, new[] { TopicValue.Invalid(raw) }));
    }

    [Fact]
    public void Default_k_is_2_matching_the_color_classifiers_debounce()
    {
        new TextStabilizerConfig("display.line*.raw").K.ShouldBe(2);
    }

    [Fact]
    public async Task Registers_derived_text_topic_from_raw_string_topic()
    {
        using var lifetime = new TestLifetime();
        var (bus, _, _) = SetUp();
        var processor = new TextStabilizerProcessor(bus, new TextStabilizerConfig("display.line*.raw"));
        _ = processor.RunAsync(lifetime.Token);

        var handle = bus.TryGetHandle("display.line1.text");
        handle.ShouldNotBeNull();
        var descriptor = bus.GetDescriptor(handle!.Value);
        descriptor.Type.ShouldBe(TopicType.String);
        descriptor.Kind.ShouldBe(TopicKind.Derived);
        descriptor.Producer.ShouldBe(TextStabilizerProcessor.ProcessorName);
        descriptor.DerivedFrom.ShouldBe(new[] { "display.line1.raw" });
    }

    [Fact]
    public async Task New_text_requires_k_consecutive_equal_samples_and_evidence_since_is_the_first_one()
    {
        using var lifetime = new TestLifetime();
        var (bus, time, raw) = SetUp();
        var processor = new TextStabilizerProcessor(bus, new TextStabilizerConfig("display.line*.raw", K: 2));
        _ = processor.RunAsync(lifetime.Token);

        var reader = TestSupport.Subscribe(bus, "display.line1.text", lifetime.Token);

        PublishRaw(bus, time, raw, "BOOT", 1); // bootstrap -> published immediately
        var t0 = time.GetTimestamp();

        time.Advance(TimeSpan.FromMilliseconds(50));
        var t1 = time.GetTimestamp();
        PublishRaw(bus, time, raw, "READY", 2); // candidate "READY", 1/2

        time.Advance(TimeSpan.FromMilliseconds(50));
        var t2 = time.GetTimestamp();
        PublishRaw(bus, time, raw, "READY", 3); // candidate "READY", 2/2 -> confirmed

        var samples = await reader.CollectSamplesAsync(2);

        samples[0].T.ShouldBe(t0);
        samples[0].Values[0].Value.AsString.ShouldBe("BOOT");
        samples[0].Values[0].EvidenceSince.ShouldBe(t0);
        samples[0].Values[0].Confidence.ShouldBeNull(); // confidence has no meaning for text, always null

        samples[1].T.ShouldBe(t2); // triggering (k-th) sample's time
        samples[1].Values[0].Value.AsString.ShouldBe("READY");
        samples[1].Values[0].EvidenceSince.ShouldBe(t1); // first of the two supporting samples
    }

    [Fact]
    public async Task Single_sample_glitch_does_not_flip_the_confirmed_text()
    {
        using var lifetime = new TestLifetime();
        var (bus, time, raw) = SetUp();
        var processor = new TextStabilizerProcessor(bus, new TextStabilizerConfig("display.line*.raw", K: 2));
        _ = processor.RunAsync(lifetime.Token);

        var reader = TestSupport.Subscribe(bus, "display.line1.text", lifetime.Token);

        PublishRaw(bus, time, raw, "READY", 1); // bootstrap -> READY

        time.Advance(TimeSpan.FromMilliseconds(50));
        PublishRaw(bus, time, raw, "REA0Y", 2); // flicker glitch, candidate 1/2

        time.Advance(TimeSpan.FromMilliseconds(50));
        PublishRaw(bus, time, raw, "READY", 3); // back to READY -> candidate cancelled, no publish (unchanged)

        time.Advance(TimeSpan.FromMilliseconds(50));
        PublishRaw(bus, time, raw, "REA0Y", 4); // glitch text restarts, candidate 1/2

        time.Advance(TimeSpan.FromMilliseconds(50));
        PublishRaw(bus, time, raw, "REA0Y", 5); // candidate 2/2 -> confirmed

        var outputs = await reader.CollectAsync(2);
        outputs[0].Value.AsString.ShouldBe("READY");
        outputs[1].Value.AsString.ShouldBe("REA0Y");
    }

    [Fact]
    public async Task Invalid_raw_sample_propagates_as_invalid_and_resets_debounce()
    {
        using var lifetime = new TestLifetime();
        var (bus, time, raw) = SetUp();
        var processor = new TextStabilizerProcessor(bus, new TextStabilizerConfig("display.line*.raw", K: 2));
        _ = processor.RunAsync(lifetime.Token);

        var reader = TestSupport.Subscribe(bus, "display.line1.text", lifetime.Token);

        PublishRaw(bus, time, raw, "READY", 1); // bootstrap -> READY

        time.Advance(TimeSpan.FromMilliseconds(50));
        PublishInvalidRaw(bus, time, raw, 2); // invalid -> propagate Invalid, reset debounce

        time.Advance(TimeSpan.FromMilliseconds(50));
        PublishRaw(bus, time, raw, "ERROR", 3); // first sample after the gap: re-bootstraps immediately

        var outputs = await reader.CollectAsync(3);

        outputs[0].Value.AsString.ShouldBe("READY");
        outputs[0].Validity.ShouldBe(Validity.Valid);

        outputs[1].Validity.ShouldBe(Validity.Invalid);

        outputs[2].Value.AsString.ShouldBe("ERROR");
        outputs[2].Validity.ShouldBe(Validity.Valid);
    }
}
