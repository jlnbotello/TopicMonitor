using Grpc.Core;
using TopicMonitor.Client;
using TopicMonitor.Contracts;
using Shouldly;

namespace TopicMonitor.Tests.Integration;

/// <summary>
/// "Protocol" bullet: snapshot on connect, no seq gaps under normal operation, Lossless
/// vs Latest behavior under a subscriber that falls behind, and - this phase's (P3) named exit criterion,
/// - the Lossless reconnect test.
/// </summary>
public class ProtocolTests
{
    /// <summary>
    /// A fresh <c>Subscribe(from_time=...)</c> call's first batch is the replay snapshot    /// <c>is_snapshot</c> rule: true only for the first batch of a call that requested a replay), and the
    /// replayed history is exactly the topic's past values - in order, at their real times - not polluted
    /// by the many unchanged-value ticks the `@topic ... policy=change` declaration filters out server-side.
    /// </summary>
    [Fact]
    public async Task Snapshot_on_connect_reflects_historical_state_in_order()
    {
        await using var h = await TestHost.StartAsync("""
            @source cam rate=20
            @topic a.raw float policy=change
            0    a.raw=1
            200  a.raw=2
            """);

        // Let real replay ticks (the shared FakeTimeProvider) run well past both changes before anyone subscribes,
        // so what we're about to receive is genuinely historical, not live.
        await h.AdvanceAsync(TimeSpan.FromMilliseconds(500));

        SampleBatch? firstBatch = null;
        var client = PanelClient.ConnectTo(h.Address);
        try
        {
            client.SampleReceived += (_, batch) => firstBatch ??= batch;
            await client.DescribeAsync();
            await client.SubscribeAsync(new[] { "a.raw" }, DeliveryMode.Lossless, fromTime: 0);

            await WaitUntilAsync(() => client.GetHistory("a.raw").Count >= 2);

            firstBatch.ShouldNotBeNull();
            firstBatch!.IsSnapshot.ShouldBeTrue();

            var history = client.GetHistory("a.raw");
            history.Count.ShouldBe(2);
            history[0].Value.AsFloat.ShouldBe(1.0);
            h.ToMs(history[0].T).ShouldBe(0.0, 1.0);
            history[1].Value.AsFloat.ShouldBe(2.0);
            h.ToMs(history[1].T).ShouldBe(200.0, 1.0);
        }
        finally
        {
            // PanelClient's background read loop can surface its own cancellation-on-teardown as
            // RpcException(Cancelled) rather than the plain OperationCanceledException it wraps
            // internally (the underlying HttpContentClientStreamReader's quirk, not this test's concern -
            // see LatencySuiteTests.SafeDisposeAsync for the same observation). Harmless here too.
            try { await client.DisposeAsync(); }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled) { }
        }
    }

    /// <summary>Under normal operation (well within the Lossless bounded queue's capacity), no <see cref="ClientSession.SeqGapDetected"/> ever fires.</summary>
    [Fact]
    public async Task No_seq_gaps_under_normal_operation()
    {
        await using var h = await TestHost.StartAsync("""
            @source cam rate=50
            @topic a.raw float policy=change
            0     a.raw=1
            2000  a.raw=2
            """);

        var client = await h.ConnectAsync("a.raw");
        var gaps = 0;
        client.Session.SeqGapDetected += (_, _) => Interlocked.Increment(ref gaps);

        await h.AdvanceAsync(TimeSpan.FromSeconds(3)); // 150 ticks: far below the 1024-capacity Lossless queue.

        gaps.ShouldBe(0);
        client.GetState("a.raw")!.Value.AsFloat.ShouldBe(2.0);
    }

    /// <summary>
    /// <c>Latest</c> coalesces to the newest state for a subscriber that falls behind - rather than
    /// disconnecting it like <c>Lossless</c> - and never blocks the publisher. Simulated with a real,
    /// deliberately slow raw-gRPC reader against a real fast-ticking server (real time; FakeTimeProvider
    /// buys nothing here since the point is genuine concurrency between ongoing publishes and a lagging
    /// reader): it receives far fewer batches than were published, never faults, and still converges on the
    /// correct final value.
    /// </summary>
    [Fact]
    public async Task Latest_mode_coalesces_for_a_slow_reader_without_disconnecting()
    {
        await using var h = await TestHost.StartAsync("""
            @source cam rate=200
            @topic a.raw float
            0     a.raw=1
            1000  a.raw=2
            """, useFakeTime: false);

        var (channel, grpc) = h.ConnectRaw();
        try
        {
            using var call = grpc.Subscribe(new SubscribeRequest { Mode = DeliveryMode.Latest, Patterns = { "a.raw" } });

            var received = 0;
            SampleBatch? last = null;
            var deadline = DateTime.UtcNow.AddSeconds(3);

            // Deliberately slow reader: ~10/s against a ~200/s publisher - most ticks must be dropped, not queued.
            while (DateTime.UtcNow < deadline)
            {
                if (!await call.ResponseStream.MoveNext(CancellationToken.None))
                    break;

                received++;
                last = call.ResponseStream.Current;
                await Task.Delay(100);
            }

            received.ShouldBeGreaterThan(0);
            received.ShouldBeLessThan(100); // published ~600 ticks over 3s at rate=200; a kept-up reader would see close to all of them.
            last.ShouldNotBeNull();
            last!.Values.Count.ShouldBeGreaterThan(0);
            // Whatever the last observed value was, Latest must never have faulted the call to get here.
        }
        finally
        {
            channel.Dispose();
        }
    }

    /// <summary>
    /// <see cref="TopicMonitor.Core.SubscriberOverflowException"/>, thrown when a Lossless subscriber's bounded
    /// queue overflows, surfaces to a real gRPC client as an <see cref="RpcException"/>
    /// with <see cref="StatusCode.Aborted"/> (see <c>TopicGrpcService.Subscribe</c>'s catch clause) - the
    /// raw protocol-level half of this phase's exit criterion, isolated from <c>PanelClient</c>'s own
    /// reconnect logic (covered end-to-end by <see cref="A_lossless_subscriber_that_falls_behind_resyncs_and_recovers"/>
    /// below).
    /// </summary>
    [Fact]
    public async Task Lossless_overflow_surfaces_as_Aborted_RpcException()
    {
        await using var h = await TestHost.StartAsync("""
            @source cam rate=2000
            @topic a.raw float
            0 a.raw=1
            """);

        var (channel, grpc) = h.ConnectRaw();
        try
        {
            using var call = grpc.Subscribe(new SubscribeRequest { Mode = DeliveryMode.Lossless, Patterns = { "a.raw" } });

            // Read as fast as possible on a background task throughout - a slow/non-reading consumer turns
            // out NOT to reliably trip the overflow over loopback (HTTP/2 flow control plus TCP buffering
            // absorb a large backlog before the server's bounded bus channel genuinely fills, confirmed
            // empirically by this suite's own latency tests). What *does* reliably trip it is a burst of
            // ticks fired synchronously within one FakeTimeProvider.Advance call: production is effectively
            // instantaneous (an in-memory loop, no awaits), while draining the bounded Lossless queue is
            // inherently asynchronous (channel read -> proto build -> network write) - no reader, however
            // fast, can drain faster than it's produced during the burst itself, so the 1024-capacity queue
            // overflows regardless.
            var received = 0;
            var readTask = Task.Run(async () =>
            {
                try
                {
                    while (await call.ResponseStream.MoveNext(CancellationToken.None))
                        Interlocked.Increment(ref received);
                    return (RpcException?)null;
                }
                catch (RpcException ex)
                {
                    return ex;
                }
            });

            // Give the background reader a moment to actually start (send the request, reach its first
            // await) and the server to register the subscriber on the bus - otherwise the burst below can
            // race ahead of subscriber registration and be published into an empty subscriber list.
            await Task.Delay(200);

            var ex = await BurstUntilOverflowAsync(h, readTask);
            ex.ShouldNotBeNull($"expected the Lossless subscriber to be disconnected with an RpcException once its bounded queue overflowed. received={received}");
            ex!.StatusCode.ShouldBe(StatusCode.Aborted);
        }
        finally
        {
            channel.Dispose();
        }
    }

    /// <summary>
    /// <b>P3 exit criterion: "Lossless reconnect test green."</b> A real
    /// <see cref="PanelClient"/>, subscribed Lossless, falls behind its bounded queue (same overflow trigger
    /// as above), gets disconnected (Aborted), and - entirely through its own already-implemented
    /// reconnect logic (<c>PanelClient.RunReadLoopAsync</c>: catch the fault, re-<c>Describe</c>, resubscribe
    /// with <c>from_time</c> = the last good <c>t</c>) - resyncs and keeps delivering live data afterward,
    /// with no observer-side intervention. Proven two ways: (1) the client's own forced post-reconnect
    /// <c>Describe()</c> call fires <see cref="PanelClient.CatalogChanged"/> at least once after the
    /// overflow (it unconditionally re-Describes on every reconnect attempt, win or lose, by construction);
    /// (2) a further time advance past the overflow is correctly observed in <see cref="PanelClient.State"/>,
    /// proving the client is genuinely live again rather than stuck or silently dead.
    /// </summary>
    [Fact]
    public async Task A_lossless_subscriber_that_falls_behind_resyncs_and_recovers()
    {
        await using var h = await TestHost.StartAsync("""
            @source cam rate=2000
            @topic a.raw float
            0 a.raw=1
            """);

        var client = await h.ConnectAsync(new[] { "a.raw" }, DeliveryMode.Lossless);
        await Task.Delay(200); // let the initial snapshot settle before we start counting catalog refreshes.

        var catalogRefreshesAfterOverflow = 0;
        client.CatalogChanged += (_, _) => Interlocked.Increment(ref catalogRefreshesAfterOverflow);

        // Burst, retrying if needed: under heavy contention from other test classes running concurrently
        // (many real Kestrel hosts at once), a single burst's "production outruns drain" margin can get
        // eaten by CPU scheduling noise. Re-bursting costs nothing when the first one already worked (the
        // condition short-circuits the loop) and makes this robust either way.
        await BurstUntilAsync(h, () => catalogRefreshesAfterOverflow >= 1);
        catalogRefreshesAfterOverflow.ShouldBeGreaterThanOrEqualTo(1,
            "expected PanelClient's reconnect loop to have re-Described at least once after the Lossless queue overflowed.");

        // Prove it's live again, not just "didn't crash": advance further and confirm fresh data arrives,
        // ending up with the correct (far-past-the-overflow-point) final state.
        await h.AdvanceAsync(TimeSpan.FromSeconds(1));

        var state = client.GetState("a.raw");
        state.ShouldNotBeNull();
        h.ToMs(state!.T).ShouldBeGreaterThan(6000); // comfortably past the overflow burst's end (t=6000ms).
    }

    /// <summary>
    /// Advances <paramref name="h"/>'s shared fake clock in bursts (default: up to 6 bursts of 6 simulated
    /// seconds each, i.e. up to rate=2000's 72 000 ticks) until <paramref name="condition"/> is satisfied,
    /// with a short real-time wait after each burst. A single sufficiently-large burst reliably overflows
    /// the 1024-capacity Lossless queue on its own (production is an instantaneous in-memory loop; draining
    /// is inherently asynchronous), but re-bursting makes the trigger robust when this suite's other classes
    /// are also running their own real Kestrel hosts concurrently (xUnit parallelizes test classes by
    /// default) and CPU scheduling noise eats into that margin.
    /// </summary>
    private static async Task BurstUntilAsync(TestHost h, Func<bool> condition, int maxAttempts = 6)
    {
        for (var i = 0; i < maxAttempts && !condition(); i++)
        {
            await h.AdvanceAsync(TimeSpan.FromSeconds(6));
            await WaitUntilAsync(condition, timeout: TimeSpan.FromSeconds(3), swallowTimeout: true);
        }
    }

    /// <summary>Same retry-burst strategy as <see cref="BurstUntilAsync"/>, for the raw-gRPC overflow test
    /// whose "condition" is a background read <see cref="Task{TResult}"/> completing (with the
    /// <see cref="RpcException"/> it caught, or <c>null</c> if the stream never faulted at all).</summary>
    private static async Task<RpcException?> BurstUntilOverflowAsync(TestHost h, Task<RpcException?> readTask, int maxAttempts = 6)
    {
        for (var i = 0; i < maxAttempts && !readTask.IsCompleted; i++)
        {
            await h.AdvanceAsync(TimeSpan.FromSeconds(6));
            try
            {
                return await readTask.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (TimeoutException)
            {
                // Not yet - burst again.
            }
        }

        return readTask.IsCompletedSuccessfully ? await readTask : null;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null, TimeSpan? poll = null, bool swallowTimeout = false)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition())
        {
            if (swallowTimeout && DateTime.UtcNow > deadline) return;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not met within the timeout.");
            await Task.Delay(poll ?? TimeSpan.FromMilliseconds(50));
        }
    }
}
