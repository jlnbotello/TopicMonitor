using TopicMonitor.Client;
using TopicMonitor.Contracts;
using Shouldly;

namespace TopicMonitor.Tests.Unit.Client;

public class TopicStateStoreTests
{
    [Fact]
    public void Apply_sets_value_confidence_validity_and_evidence_since_from_a_batch()
    {
        var store = new TopicStateStore();
        var batch = new SampleBatch
        {
            T = 100,
            TPrev = 50,
            TProcessed = 95,
            TPublish = 100,
            TUtcUs = 1000,
            Values =
            {
                new TopicValue
                {
                    Topic = 1,
                    EnumIndex = 2,
                    Confidence = 0.9f,
                    Validity = Validity.Valid,
                    EvidenceSince = 80,
                },
            },
        };

        store.Apply(batch);

        var state = store.Get(1);
        state.ShouldNotBeNull();
        state!.Value.AsEnumIndex.ShouldBe(2u);
        state.Confidence.ShouldBe(0.9f);
        state.Validity.ShouldBe(Validity.Valid);
        state.EvidenceSince.ShouldBe(80);
        state.T.ShouldBe(100);
        state.TPrev.ShouldBe(50);
        state.TProcessed.ShouldBe(95);
    }

    [Fact]
    public void Apply_without_optional_confidence_or_evidence_since_leaves_them_null()
    {
        var store = new TopicStateStore();
        store.Apply(new SampleBatch
        {
            Values = { new TopicValue { Topic = 1, I = 1, Validity = Validity.Valid } },
        });

        var state = store.Get(1)!;
        state.Confidence.ShouldBeNull();
        state.EvidenceSince.ShouldBeNull();
    }

    [Fact]
    public void A_later_batch_overwrites_the_earlier_state_for_the_same_topic()
    {
        var store = new TopicStateStore();
        store.Apply(new SampleBatch { T = 0, Values = { new TopicValue { Topic = 1, I = 1, Validity = Validity.Valid } } });
        store.Apply(new SampleBatch { T = 100, Values = { new TopicValue { Topic = 1, I = 2, Validity = Validity.Invalid } } });

        var state = store.Get(1)!;
        state.Value.AsInt.ShouldBe(2);
        state.Validity.ShouldBe(Validity.Invalid);
        state.T.ShouldBe(100);
    }

    [Fact]
    public void Unknown_topic_returns_null()
    {
        var store = new TopicStateStore();
        store.Get(999).ShouldBeNull();
        store.TryGet(999, out var state).ShouldBeFalse();
        state.ShouldBeNull();
    }

    [Fact]
    public void Snapshot_returns_the_latest_state_of_every_known_topic()
    {
        var store = new TopicStateStore();
        store.Apply(new SampleBatch
        {
            Values =
            {
                new TopicValue { Topic = 1, I = 1, Validity = Validity.Valid },
                new TopicValue { Topic = 2, I = 2, Validity = Validity.Valid },
            },
        });

        store.Snapshot().Select(s => s.TopicId).OrderBy(x => x).ShouldBe(new[] { 1u, 2u });
    }
}
