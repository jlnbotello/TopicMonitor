using Microsoft.Extensions.Time.Testing;
using TopicMonitor.Core;
using TopicMonitor.Processors;
using Shouldly;

namespace TopicMonitor.Tests.Unit.Processors;

public class BlinkDetectorProcessorTests
{
    private static (TopicBus Bus, FakeTimeProvider Time, TopicHandle Color) SetUp()
    {
        var time = new FakeTimeProvider();
        var bus = new TopicBus(time, TimeSpan.FromSeconds(30));
        var color = bus.Register(new TopicDescriptor("led.1.color", TopicType.Enum, "color-classifier",
            TopicKind.Derived, EnumValues: new[] { "off", "red", "green", "yellow" }));
        return (bus, time, color);
    }

    private static void PublishColor(TopicBus bus, FakeTimeProvider time, TopicHandle color, string value, ulong seq)
    {
        var t = time.GetTimestamp();
        bus.Publish(new Sample(new SourceId("color-classifier"), seq, t, t, t,
            new[] { new TopicValue(color, Value.OfEnum(value), 1f, Validity.Valid, null) }));
    }

    private static void PublishInvalidColor(TopicBus bus, FakeTimeProvider time, TopicHandle color, ulong seq)
    {
        var t = time.GetTimestamp();
        bus.Publish(new Sample(new SourceId("color-classifier"), seq, t, t, t, new[] { TopicValue.Invalid(color) }));
    }

    [Fact]
    public async Task Registers_derived_state_and_freq_topics_from_color_topic()
    {
        using var lifetime = new TestLifetime();
        var (bus, _, _) = SetUp();
        var processor = new BlinkDetectorProcessor(bus, new FakeTimeProvider(), new BlinkDetectorConfig("led.*.color"));
        _ = processor.RunAsync(lifetime.Token);

        var stateHandle = bus.TryGetHandle("led.1.state");
        stateHandle.ShouldNotBeNull();
        var stateDescriptor = bus.GetDescriptor(stateHandle!.Value);
        stateDescriptor.Type.ShouldBe(TopicType.Enum);
        stateDescriptor.Kind.ShouldBe(TopicKind.Derived);
        stateDescriptor.Producer.ShouldBe(BlinkDetectorProcessor.ProcessorName);
        stateDescriptor.EnumValues.ShouldBe(new[] { "off", "steady", "blinking" }, ignoreOrder: true);
        stateDescriptor.DerivedFrom.ShouldBe(new[] { "led.1.color" });

        var freqHandle = bus.TryGetHandle("led.1.freq");
        freqHandle.ShouldNotBeNull();
        var freqDescriptor = bus.GetDescriptor(freqHandle!.Value);
        freqDescriptor.Type.ShouldBe(TopicType.Float);
        freqDescriptor.Unit.ShouldBe("Hz");
        freqDescriptor.EffectivePolicy.Kind.ShouldBe(PublishPolicyKind.Deadband);
        freqDescriptor.EffectivePolicy.DeadbandThreshold.ShouldBe(0.5);
    }

    [Fact]
    public async Task Default_frequency_deadband_is_half_a_hertz()
    {
        new BlinkDetectorConfig("led.*.color").FrequencyDeadbandHz.ShouldBe(0.5);
    }

    [Fact]
    public async Task Off_color_with_no_transitions_yields_off_state()
    {
        using var lifetime = new TestLifetime();
        var (bus, time, color) = SetUp();
        var processor = new BlinkDetectorProcessor(bus, time, new BlinkDetectorConfig("led.*.color"));
        _ = processor.RunAsync(lifetime.Token);

        var stateReader = TestSupport.Subscribe(bus, "led.1.state", lifetime.Token);
        PublishColor(bus, time, color, "off", 1);

        var states = await stateReader.CollectAsync(1);
        states[0].Value.AsEnum.ShouldBe("off");
    }

    [Fact]
    public async Task Non_off_color_with_no_transitions_yields_steady_state()
    {
        using var lifetime = new TestLifetime();
        var (bus, time, color) = SetUp();
        var processor = new BlinkDetectorProcessor(bus, time, new BlinkDetectorConfig("led.*.color"));
        _ = processor.RunAsync(lifetime.Token);

        var stateReader = TestSupport.Subscribe(bus, "led.1.state", lifetime.Token);
        PublishColor(bus, time, color, "red", 1);

        var states = await stateReader.CollectAsync(1);
        states[0].Value.AsEnum.ShouldBe("steady");
    }

    [Fact]
    public async Task Regular_alternating_color_is_detected_as_blinking_after_one_period_with_correct_frequency()
    {
        using var lifetime = new TestLifetime();
        var (bus, time, color) = SetUp();
        var processor = new BlinkDetectorProcessor(bus, time, new BlinkDetectorConfig("led.*.color"));
        _ = processor.RunAsync(lifetime.Token);

        var stateReader = TestSupport.Subscribe(bus, "led.1.state", lifetime.Token);
        var freqReader = TestSupport.Subscribe(bus, "led.1.freq", lifetime.Token);

        // off -> red -> off -> red, each half-period = 100ms, i.e. a 200ms (5 Hz) blink.
        PublishColor(bus, time, color, "off", 1); // t=0: bootstrap -> off
        var t0 = time.GetTimestamp();

        time.Advance(TimeSpan.FromMilliseconds(100));
        PublishColor(bus, time, color, "red", 2); // t=100: 1st transition -> not enough history yet -> steady

        time.Advance(TimeSpan.FromMilliseconds(100));
        PublishColor(bus, time, color, "off", 3); // t=200: 2nd transition completes one full period (off->red->off) -> blinking

        time.Advance(TimeSpan.FromMilliseconds(100));
        PublishColor(bus, time, color, "red", 4); // t=300: 3rd transition, still periodic -> blinking, freq unchanged (deadband drops it)

        // State topic has an OnChange bus policy: off, steady, blinking are each published once; the 2nd
        // "blinking" at t=300 is dropped as unchanged.
        var states = await stateReader.CollectAsync(3);
        states[0].Value.AsEnum.ShouldBe("off");
        states[0].EvidenceSince.ShouldBe(t0);

        states[1].Value.AsEnum.ShouldBe("steady");

        states[2].Value.AsEnum.ShouldBe("blinking");

        // Freq topic has a 0.5 Hz deadband policy: 0 (bootstrap), then 5.0 Hz once periodicity is confirmed;
        // the repeated 5.0 Hz at t=300 is dropped as an unchanged value (delta 0 < 0.5 Hz deadband).
        var freqs = await freqReader.CollectAsync(2);
        freqs[0].Value.AsFloat.ShouldBe(0.0);
        freqs[1].Value.AsFloat.ShouldBe(5.0, 0.01);
    }

    [Fact]
    public async Task Irregular_transitions_are_not_classified_as_blinking()
    {
        using var lifetime = new TestLifetime();
        var (bus, time, color) = SetUp();
        var processor = new BlinkDetectorProcessor(bus, time, new BlinkDetectorConfig("led.*.color"));
        _ = processor.RunAsync(lifetime.Token);

        var stateReader = TestSupport.Subscribe(bus, "led.1.state", lifetime.Token);

        PublishColor(bus, time, color, "off", 1); // bootstrap -> off

        time.Advance(TimeSpan.FromMilliseconds(100));
        PublishColor(bus, time, color, "red", 2); // -> steady (not enough history)

        // Three different colors in a row (off -> red -> green): the last and 3rd-from-last transition
        // colors don't match, so this never looks periodic.
        time.Advance(TimeSpan.FromMilliseconds(100));
        PublishColor(bus, time, color, "green", 3); // -> steady (off/red/green: no repeat -> not periodic)

        var states = await stateReader.CollectAsync(2);
        states[0].Value.AsEnum.ShouldBe("off");
        states[1].Value.AsEnum.ShouldBe("steady"); // never reaches "blinking"
    }

    [Fact]
    public async Task Invalid_color_sample_propagates_as_invalid_and_resets_transition_history()
    {
        using var lifetime = new TestLifetime();
        var (bus, time, color) = SetUp();
        var processor = new BlinkDetectorProcessor(bus, time, new BlinkDetectorConfig("led.*.color"));
        _ = processor.RunAsync(lifetime.Token);

        var stateReader = TestSupport.Subscribe(bus, "led.1.state", lifetime.Token);
        var freqReader = TestSupport.Subscribe(bus, "led.1.freq", lifetime.Token);

        PublishColor(bus, time, color, "red", 1); // bootstrap -> steady

        time.Advance(TimeSpan.FromMilliseconds(50));
        PublishInvalidColor(bus, time, color, 2); // invalid -> propagate Invalid, reset history

        time.Advance(TimeSpan.FromMilliseconds(50));
        PublishColor(bus, time, color, "off", 3); // first sample after the gap: re-bootstraps immediately

        var states = await stateReader.CollectAsync(3);
        states[0].Value.AsEnum.ShouldBe("steady");
        states[1].Validity.ShouldBe(Validity.Invalid);
        states[2].Value.AsEnum.ShouldBe("off");

        // Deadband policy always re-publishes on a validity change, even if the numeric value repeats:
        // bootstrap freq=0 (valid), then Invalid, then freq=0 again (valid) after the re-bootstrap.
        var freqs = await freqReader.CollectAsync(3);
        freqs[0].Value.AsFloat.ShouldBe(0.0);
        freqs[0].Validity.ShouldBe(Validity.Valid);
        freqs[1].Validity.ShouldBe(Validity.Invalid);
        freqs[2].Value.AsFloat.ShouldBe(0.0);
        freqs[2].Validity.ShouldBe(Validity.Valid);
    }
}
