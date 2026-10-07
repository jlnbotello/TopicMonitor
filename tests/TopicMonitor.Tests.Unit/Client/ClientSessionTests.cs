using Microsoft.Extensions.Time.Testing;
using TopicMonitor.Client;
using TopicMonitor.Contracts;
using Shouldly;

namespace TopicMonitor.Tests.Unit.Client;

public class ClientSessionTests
{
    private static SampleBatch Batch(
        ulong seq,
        string source,
        long t,
        bool isSnapshot = false,
        ulong catalogVersion = 1,
        IEnumerable<TopicValue>? values = null,
        long tProcessed = 0)
    {
        var batch = new SampleBatch
        {
            Seq = seq,
            Source = source,
            T = t,
            TPrev = t,
            TProcessed = tProcessed,
            TPublish = t,
            TUtcUs = t,
            IsSnapshot = isSnapshot,
            CatalogVersion = catalogVersion,
        };
        if (values is not null) batch.Values.AddRange(values);
        return batch;
    }

    private static TopicValue IntValue(uint topic, long v, float? confidence = null, Validity validity = Validity.Valid, long? evidenceSince = null)
    {
        var tv = new TopicValue { Topic = topic, I = v, Validity = validity };
        if (confidence is { } c) tv.Confidence = c;
        if (evidenceSince is { } e) tv.EvidenceSince = e;
        return tv;
    }

    private static TopicValue EnumValue(uint topic, uint index) => new() { Topic = topic, EnumIndex = index, Validity = Validity.Valid };

    [Fact]
    public void Ingest_updates_state_store_value_confidence_validity_and_timestamps()
    {
        var session = new ClientSession();

        session.Ingest(Batch(1, "cam", t: 100, isSnapshot: true,
            values: new[] { IntValue(7, 42, confidence: 0.8f, evidenceSince: 90) },
            tProcessed: 95));

        var state = session.State.Get(7);
        state.ShouldNotBeNull();
        state!.Value.AsInt.ShouldBe(42);
        state.Confidence.ShouldBe(0.8f);
        state.Validity.ShouldBe(Validity.Valid);
        state.EvidenceSince.ShouldBe(90);
        state.T.ShouldBe(100);
        state.TProcessed.ShouldBe(95);
    }

    [Fact]
    public void Ingest_applies_later_batch_values_over_earlier_ones()
    {
        var session = new ClientSession();

        session.Ingest(Batch(1, "cam", t: 100, isSnapshot: true, values: new[] { IntValue(1, 1) }));
        session.Ingest(Batch(2, "cam", t: 200, values: new[] { IntValue(1, 2) }));

        session.State.Get(1)!.Value.AsInt.ShouldBe(2);
    }

    [Fact]
    public void Ingest_with_empty_values_is_a_tick_and_does_not_change_state()
    {
        var session = new ClientSession();
        session.Ingest(Batch(1, "cam", t: 100, isSnapshot: true, values: new[] { IntValue(1, 5) }));

        session.Ingest(Batch(2, "cam", t: 150)); // tick: no values

        session.State.Get(1)!.Value.AsInt.ShouldBe(5);
    }

    [Fact]
    public void Ingest_records_history_point_per_topic_in_order()
    {
        var session = new ClientSession();

        session.Ingest(Batch(1, "cam", t: 0, isSnapshot: true, values: new[] { EnumValue(3, 0) }));
        session.Ingest(Batch(2, "cam", t: 100, values: new[] { EnumValue(3, 1) }));
        session.Ingest(Batch(3, "cam", t: 200, values: new[] { EnumValue(3, 0) }));

        var history = session.History.Get(3);
        history.Count.ShouldBe(3);
        history.Select(p => (p.T, p.Value.AsEnumIndex)).ShouldBe(new[] { (0L, 0u), (100L, 1u), (200L, 0u) });
    }

    [Fact]
    public void Ingest_tracks_last_received_t_as_the_maximum_seen()
    {
        var session = new ClientSession();

        session.Ingest(Batch(1, "cam", t: 100, isSnapshot: true));
        session.Ingest(Batch(2, "cam", t: 300));

        session.LastReceivedT.ShouldBe(300);
    }

    [Fact]
    public void No_seq_gap_for_consecutive_batches_on_the_same_source()
    {
        var session = new ClientSession();
        var fired = false;
        session.SeqGapDetected += (_, _) => fired = true;

        session.Ingest(Batch(1, "cam", t: 0, isSnapshot: true));
        session.Ingest(Batch(2, "cam", t: 50));
        session.Ingest(Batch(3, "cam", t: 100));

        fired.ShouldBeFalse();
    }

    [Fact]
    public void Seq_gap_is_detected_when_a_batch_is_skipped()
    {
        var session = new ClientSession();
        SeqGapEventArgs? captured = null;
        session.SeqGapDetected += (_, e) => captured = e;

        session.Ingest(Batch(1, "cam", t: 0, isSnapshot: true));
        session.Ingest(Batch(5, "cam", t: 200)); // expected 2, got 5

        captured.ShouldNotBeNull();
        captured!.Source.ShouldBe("cam");
        captured.ExpectedSeq.ShouldBe(2UL);
        captured.ActualSeq.ShouldBe(5UL);
    }

    [Fact]
    public void Snapshot_batches_never_count_as_a_seq_gap()
    {
        var session = new ClientSession();
        var fired = false;
        session.SeqGapDetected += (_, _) => fired = true;

        session.Ingest(Batch(1, "cam", t: 0, isSnapshot: true));
        session.Ingest(Batch(2, "cam", t: 50));
        // Reconnect: a new snapshot arrives with an unrelated seq; must not be flagged as a gap.
        session.Ingest(Batch(999, "cam", t: 100, isSnapshot: true));

        fired.ShouldBeFalse();
    }

    [Fact]
    public void Two_interleaved_sources_are_tracked_independently_without_false_gaps()
    {
        var session = new ClientSession();
        var gaps = new List<SeqGapEventArgs>();
        session.SeqGapDetected += (_, e) => gaps.Add(e);

        session.Ingest(Batch(1, "cam-a", t: 0, isSnapshot: true));
        session.Ingest(Batch(1, "cam-b", t: 0, isSnapshot: true));
        session.Ingest(Batch(2, "cam-a", t: 50));
        session.Ingest(Batch(2, "cam-b", t: 50));
        session.Ingest(Batch(3, "cam-a", t: 100));
        session.Ingest(Batch(3, "cam-b", t: 100));

        gaps.ShouldBeEmpty();
    }

    [Fact]
    public void A_gap_on_one_source_does_not_affect_tracking_of_another_source()
    {
        var session = new ClientSession();
        var gaps = new List<SeqGapEventArgs>();
        session.SeqGapDetected += (_, e) => gaps.Add(e);

        session.Ingest(Batch(1, "cam-a", t: 0, isSnapshot: true));
        session.Ingest(Batch(1, "cam-b", t: 0, isSnapshot: true));
        session.Ingest(Batch(2, "cam-b", t: 50)); // cam-b fine
        session.Ingest(Batch(9, "cam-a", t: 50)); // cam-a: gap, expected 2 got 9

        gaps.Count.ShouldBe(1);
        gaps[0].Source.ShouldBe("cam-a");
        gaps[0].ExpectedSeq.ShouldBe(2UL);
        gaps[0].ActualSeq.ShouldBe(9UL);
    }

    [Fact]
    public void ResetSeqTracking_clears_baseline_so_next_batch_is_not_flagged()
    {
        var session = new ClientSession();
        var fired = false;
        session.SeqGapDetected += (_, _) => fired = true;

        session.Ingest(Batch(1, "cam", t: 0, isSnapshot: true));
        session.Ingest(Batch(2, "cam", t: 50));

        session.ResetSeqTracking();
        session.Ingest(Batch(777, "cam", t: 100)); // would be a gap without the reset

        fired.ShouldBeFalse();
    }

    [Fact]
    public void First_batch_never_raises_catalog_version_changed()
    {
        var session = new ClientSession();
        var fired = false;
        session.CatalogVersionChanged += (_, _) => fired = true;

        session.Ingest(Batch(1, "cam", t: 0, isSnapshot: true, catalogVersion: 1));

        fired.ShouldBeFalse();
    }

    [Fact]
    public void Catalog_version_change_raises_event_with_old_and_new_version()
    {
        var session = new ClientSession();
        CatalogVersionChangedEventArgs? captured = null;
        session.CatalogVersionChanged += (_, e) => captured = e;

        session.Ingest(Batch(1, "cam", t: 0, isSnapshot: true, catalogVersion: 1));
        session.Ingest(Batch(2, "cam", t: 50, catalogVersion: 1));
        session.Ingest(Batch(3, "cam", t: 100, catalogVersion: 2));

        captured.ShouldNotBeNull();
        captured!.OldVersion.ShouldBe(1UL);
        captured.NewVersion.ShouldBe(2UL);
    }

    [Fact]
    public void Catalog_version_change_is_a_testable_hook_not_a_literal_network_call()
    {
        // Simulates how PanelClient wires re-Describe() without any gRPC: the session only raises an
        // event, the caller decides what "resync" means.
        var session = new ClientSession();
        var describeCalls = 0;
        session.CatalogVersionChanged += (_, _) => describeCalls++;

        session.Ingest(Batch(1, "cam", t: 0, isSnapshot: true, catalogVersion: 1));
        session.Ingest(Batch(2, "cam", t: 50, catalogVersion: 2));
        session.Ingest(Batch(3, "cam", t: 100, catalogVersion: 2));
        session.Ingest(Batch(4, "cam", t: 150, catalogVersion: 3));

        describeCalls.ShouldBe(2);
    }

    [Fact]
    public void SampleApplied_fires_once_per_ingested_batch()
    {
        var session = new ClientSession();
        var count = 0;
        session.SampleApplied += (_, _) => count++;

        session.Ingest(Batch(1, "cam", t: 0, isSnapshot: true));
        session.Ingest(Batch(2, "cam", t: 50));
        session.Ingest(Batch(3, "cam", t: 100));

        count.ShouldBe(3);
    }

    [Fact]
    public void Latency_probe_records_recv_minus_t_processed_using_the_injected_time_provider()
    {
        var time = new FakeTimeProvider();
        var session = new ClientSession(time);

        time.SetUtcNow(time.GetUtcNow().AddMilliseconds(123));
        var recvBefore = time.GetTimestamp();

        session.Ingest(Batch(1, "cam", t: 0, isSnapshot: true, tProcessed: recvBefore - 10));

        var sample = session.Latency.Snapshot().Single();
        sample.LatencyTicks.ShouldBe(10);
    }
}
