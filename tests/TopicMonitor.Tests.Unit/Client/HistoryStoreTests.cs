using TopicMonitor.Client;
using TopicMonitor.Contracts;
using Shouldly;

namespace TopicMonitor.Tests.Unit.Client;

public class HistoryStoreTests
{
    private static SampleBatch Batch(long t, uint topic, long value) =>
        new()
        {
            Seq = (ulong)t,
            Source = "cam",
            T = t,
            TPrev = t,
            TProcessed = t,
            TPublish = t,
            TUtcUs = t,
            Values = { new TopicValue { Topic = topic, I = value, Validity = Validity.Valid } },
        };

    [Fact]
    public void Get_returns_points_in_arrival_order()
    {
        var store = new HistoryStore();

        store.Apply(Batch(0, 1, 10));
        store.Apply(Batch(100, 1, 20));
        store.Apply(Batch(200, 1, 30));

        store.Get(1).Select(p => p.T).ShouldBe(new[] { 0L, 100L, 200L });
    }

    [Fact]
    public void Get_is_empty_for_an_unknown_topic()
    {
        var store = new HistoryStore();
        store.Get(42).ShouldBeEmpty();
    }

    [Fact]
    public void A_tick_batch_with_no_values_adds_no_history_point()
    {
        var store = new HistoryStore();
        store.Apply(Batch(0, 1, 10));

        var tick = new SampleBatch { Seq = 2, Source = "cam", T = 50 }; // empty Values = tick
        store.Apply(tick);

        store.Get(1).Count.ShouldBe(1);
    }

    [Fact]
    public void Retention_bounds_the_series_to_MaxPointsPerTopic_keeping_the_newest()
    {
        var store = new HistoryStore(maxPointsPerTopic: 3);

        for (var t = 0; t < 10; t++) store.Apply(Batch(t, 1, t));

        var points = store.Get(1);
        points.Count.ShouldBe(3);
        points.Select(p => p.T).ShouldBe(new[] { 7L, 8L, 9L });
    }

    [Fact]
    public void Each_topic_has_an_independent_series()
    {
        var store = new HistoryStore();

        var batch = new SampleBatch
        {
            Seq = 1,
            Source = "cam",
            T = 0,
            Values =
            {
                new TopicValue { Topic = 1, I = 11, Validity = Validity.Valid },
                new TopicValue { Topic = 2, I = 22, Validity = Validity.Valid },
            },
        };
        store.Apply(batch);

        store.Get(1).Single().Value.AsInt.ShouldBe(11);
        store.Get(2).Single().Value.AsInt.ShouldBe(22);
    }

    [Fact]
    public void Constructor_rejects_non_positive_capacity()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new HistoryStore(0));
        Should.Throw<ArgumentOutOfRangeException>(() => new HistoryStore(-1));
    }

    [Fact]
    public void Clear_empties_all_series()
    {
        var store = new HistoryStore();
        store.Apply(Batch(0, 1, 10));

        store.Clear();

        store.Get(1).ShouldBeEmpty();
    }
}
