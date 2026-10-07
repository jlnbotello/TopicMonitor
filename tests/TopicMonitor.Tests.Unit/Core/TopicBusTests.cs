using Microsoft.Extensions.Time.Testing;
using TopicMonitor.Core;
using Shouldly;

namespace TopicMonitor.Tests.Unit.Core;

public class TopicBusTests
{
    private static TopicBus CreateBus(FakeTimeProvider time) => new(time, TimeSpan.FromSeconds(1));

    [Fact]
    public void Register_assigns_stable_handle_and_bumps_catalog_version()
    {
        var time = new FakeTimeProvider();
        var bus = CreateBus(time);

        var h1 = bus.Register(new TopicDescriptor("led.1.raw", TopicType.Vec, "file", Components: new[] { "r", "g", "b" }));
        var catalogAfterFirst = bus.GetCatalog();
        var h2 = bus.Register(new TopicDescriptor("led.2.raw", TopicType.Vec, "file", Components: new[] { "r", "g", "b" }));
        var catalogAfterSecond = bus.GetCatalog();

        h1.ShouldNotBe(h2);
        catalogAfterSecond.CatalogVersion.ShouldBeGreaterThan(catalogAfterFirst.CatalogVersion);
        catalogAfterSecond.Topics.Count.ShouldBe(2);
    }

    [Fact]
    public void Register_duplicate_name_throws()
    {
        var bus = CreateBus(new FakeTimeProvider());
        bus.Register(new TopicDescriptor("led.1.raw", TopicType.Vec, "file"));

        Should.Throw<DuplicateTopicException>(() =>
            bus.Register(new TopicDescriptor("led.1.raw", TopicType.Float, "processor")));
    }

    [Fact]
    public async Task Subscribe_receives_live_samples_matching_pattern()
    {
        var time = new FakeTimeProvider();
        var bus = CreateBus(time);
        var raw = bus.Register(new TopicDescriptor("led.1.raw", TopicType.Vec, "file"));
        var other = bus.Register(new TopicDescriptor("led.2.raw", TopicType.Vec, "file"));

        using var cts = new CancellationTokenSource();
        var enumerator = bus.Subscribe(new TopicFilter(new[] { "led.1.*" }), null, DeliveryMode.Lossless, cts.Token)
            .GetAsyncEnumerator(cts.Token);

        var t = time.GetTimestamp();
        bus.Publish(new Sample(new SourceId("file"), 1, t, t, t, new[]
        {
            new TopicValue(raw, Value.OfVec(new[] { 1.0, 2.0, 3.0 }), null, Validity.Valid, null),
            new TopicValue(other, Value.OfVec(new[] { 9.0, 9.0, 9.0 }), null, Validity.Valid, null),
        }));

        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        var received = enumerator.Current;
        received.Values.Count.ShouldBe(1);
        received.Values[0].Topic.ShouldBe(raw);
    }

    [Fact]
    public async Task Subscribe_with_fromTime_replays_history_then_live()
    {
        var time = new FakeTimeProvider();
        var bus = CreateBus(time);
        var topic = bus.Register(new TopicDescriptor("led.1.color", TopicType.Enum, "proc", EnumValues: new[] { "off", "red" }));

        var t0 = time.GetTimestamp();
        bus.Publish(new Sample(new SourceId("proc"), 1, t0, t0, t0, new[]
        {
            new TopicValue(topic, Value.OfEnum("off"), null, Validity.Valid, null),
        }));

        time.Advance(TimeSpan.FromMilliseconds(100));
        var t1 = time.GetTimestamp();

        using var cts = new CancellationTokenSource();
        var enumerator = bus.Subscribe(TopicFilter.All, t0, DeliveryMode.Lossless, cts.Token).GetAsyncEnumerator(cts.Token);

        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        enumerator.Current.Values[0].Value.AsEnum.ShouldBe("off");

        bus.Publish(new Sample(new SourceId("proc"), 2, t1, t0, t1, new[]
        {
            new TopicValue(topic, Value.OfEnum("red"), null, Validity.Valid, null),
        }));

        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        enumerator.Current.Values[0].Value.AsEnum.ShouldBe("red");
    }

    [Fact]
    public void SetSourceInvalid_marks_all_of_sources_topics_invalid()
    {
        var time = new FakeTimeProvider();
        var bus = CreateBus(time);
        var a = bus.Register(new TopicDescriptor("led.1.raw", TopicType.Vec, "file"));
        var b = bus.Register(new TopicDescriptor("led.2.raw", TopicType.Vec, "file"));

        bus.SetSourceInvalid(new SourceId("file"));

        var history = typeof(TopicBus).GetField("_history", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(bus) as HistoryBuffer;
        var samples = history!.Snapshot();
        samples.Count.ShouldBe(1);
        samples[0].Values.Count.ShouldBe(2);
        samples[0].Values.All(v => v.Validity == Validity.Invalid).ShouldBeTrue();
    }

    [Fact]
    public void OnChange_policy_drops_repeated_equal_values()
    {
        var time = new FakeTimeProvider();
        var bus = CreateBus(time);
        var topic = bus.Register(new TopicDescriptor("display.line1.text", TopicType.String, "proc", Policy: PublishPolicy.OnChange));

        var t = time.GetTimestamp();
        bus.Publish(new Sample(new SourceId("proc"), 1, t, t, t, new[] { new TopicValue(topic, Value.OfString("READY"), null, Validity.Valid, null) }));
        bus.Publish(new Sample(new SourceId("proc"), 2, t, t, t, new[] { new TopicValue(topic, Value.OfString("READY"), null, Validity.Valid, null) }));
        bus.Publish(new Sample(new SourceId("proc"), 3, t, t, t, new[] { new TopicValue(topic, Value.OfString("BOOT"), null, Validity.Valid, null) }));

        var history = typeof(TopicBus).GetField("_history", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(bus) as HistoryBuffer;
        var samples = history!.Snapshot();

        samples.Count(s => s.Values.Count > 0).ShouldBe(2);
    }

    [Fact]
    public void Deadband_policy_drops_small_changes()
    {
        var time = new FakeTimeProvider();
        var bus = CreateBus(time);
        var topic = bus.Register(new TopicDescriptor("sensor.temp", TopicType.Float, "proc", Policy: PublishPolicy.Deadband(1.0)));

        var t = time.GetTimestamp();
        bus.Publish(new Sample(new SourceId("proc"), 1, t, t, t, new[] { new TopicValue(topic, Value.OfFloat(20.0), null, Validity.Valid, null) }));
        bus.Publish(new Sample(new SourceId("proc"), 2, t, t, t, new[] { new TopicValue(topic, Value.OfFloat(20.5), null, Validity.Valid, null) }));
        bus.Publish(new Sample(new SourceId("proc"), 3, t, t, t, new[] { new TopicValue(topic, Value.OfFloat(22.0), null, Validity.Valid, null) }));

        var history = typeof(TopicBus).GetField("_history", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(bus) as HistoryBuffer;
        var samples = history!.Snapshot();

        samples.Count(s => s.Values.Count > 0).ShouldBe(2);
    }

    [Fact]
    public void History_prunes_samples_older_than_retention_window()
    {
        var time = new FakeTimeProvider();
        var bus = new TopicBus(time, TimeSpan.FromMilliseconds(100));
        var topic = bus.Register(new TopicDescriptor("led.1.raw", TopicType.Vec, "file"));

        var t0 = time.GetTimestamp();
        bus.Publish(new Sample(new SourceId("file"), 1, t0, t0, t0, new[] { new TopicValue(topic, Value.OfVec(new[] { 0.0, 0.0, 0.0 }), null, Validity.Valid, null) }));

        time.Advance(TimeSpan.FromMilliseconds(200));
        var t1 = time.GetTimestamp();
        bus.Publish(new Sample(new SourceId("file"), 2, t1, t0, t1, new[] { new TopicValue(topic, Value.OfVec(new[] { 1.0, 1.0, 1.0 }), null, Validity.Valid, null) }));

        var history = typeof(TopicBus).GetField("_history", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(bus) as HistoryBuffer;
        var samples = history!.Snapshot();

        samples.Count.ShouldBe(1);
        samples[0].T.ShouldBe(t1);
    }
}
