using TopicMonitor.Core;
using Shouldly;

namespace TopicMonitor.Tests.Unit.Processors;

/// <summary>
/// Shared plumbing for driving a real <see cref="TopicBus"/> end-to-end in processor tests (publish raw
/// samples, read back derived samples), ("most faithful way to exercise the k-1 sample
/// delay timing claims").
/// </summary>
internal static class TestSupport
{
    /// <summary>
    /// Reads samples live from <paramref name="bus"/> matching <paramref name="pattern"/>, starting the
    /// subscription synchronously so callers can publish immediately afterward without racing the
    /// subscriber's registration (see <see cref="SubscriptionReader.Start"/>).
    /// </summary>
    public static SubscriptionReader Subscribe(ITopicBus bus, string pattern, CancellationToken cancellationToken = default) =>
        SubscriptionReader.Start(bus, pattern, cancellationToken);
}

/// <summary>
/// <c>using var lifetime = new TestLifetime();</c> at the top of a test: its token drives both the
/// processor's <c>RunAsync</c> loop and any <see cref="SubscriptionReader"/>s, and cancelling it on
/// dispose (including when an assertion throws) lets each subscription's bus registration unwind via its
/// <c>finally</c> block instead of leaking a background task that awaits a channel nothing will ever
/// write to again - which otherwise keeps the test process alive past the end of the test.
/// </summary>
internal sealed class TestLifetime : IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    public CancellationToken Token => _cts.Token;

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}

/// <summary>
/// Wraps a live bus subscription so tests can deterministically await the next N delivered
/// <see cref="TopicValue"/>s (flattened across however many <see cref="Sample"/>s that takes; empty-Values
/// "tick" samples contribute nothing and are skipped automatically) without sleeps or polling.
///
/// <see cref="ITopicBus.Subscribe"/> returns a lazy <c>IAsyncEnumerable</c>: nothing in its body runs -
/// in particular the subscriber is not yet registered with the bus - until the first
/// <c>MoveNextAsync()</c> call. <see cref="Start"/> kicks that first call off immediately (without
/// awaiting it) so the synchronous prologue (subscriber registration) has definitely run by the time
/// <see cref="Start"/> returns, before the caller publishes anything the subscription is meant to observe.
///
/// Not <see cref="IAsyncDisposable"/>: the reader always keeps one outstanding, unawaited
/// <c>MoveNextAsync()</c> call pending (so the next publish can be observed without re-registering), and
/// the compiler-generated async-iterator for <see cref="ITopicBus.Subscribe"/> throws
/// <see cref="NotSupportedException"/> if disposed while a call is outstanding. Tests are short-lived, so
/// the subscription is simply left for the garbage collector once the bus/test go out of scope.
/// </summary>
internal sealed class SubscriptionReader
{
    private readonly IAsyncEnumerator<Sample> _enumerator;
    private ValueTask<bool> _pending;

    private SubscriptionReader(IAsyncEnumerator<Sample> enumerator, ValueTask<bool> pending)
    {
        _enumerator = enumerator;
        _pending = pending;
    }

    public static SubscriptionReader Start(ITopicBus bus, string pattern, CancellationToken cancellationToken = default)
    {
        var enumerator = bus.Subscribe(new TopicFilter(new[] { pattern }), null, DeliveryMode.Lossless, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        var pending = enumerator.MoveNextAsync(); // forces synchronous subscriber registration, see remarks above
        return new SubscriptionReader(enumerator, pending);
    }

    /// <summary>Awaits the next sample that actually carries values (skipping empty-Values "tick" samples).</summary>
    private async Task<Sample> NextNonTickSampleAsync()
    {
        while (true)
        {
            var hasNext = await _pending;
            hasNext.ShouldBeTrue("subscription ended before the expected data arrived.");
            var sample = _enumerator.Current;
            _pending = _enumerator.MoveNextAsync();
            if (sample.Values.Count > 0) return sample;
        }
    }

    /// <summary>Awaits delivered samples until at least <paramref name="count"/> TopicValues have been
    /// observed in total (flattened across however many samples that takes), then returns exactly that many.</summary>
    public async Task<List<TopicValue>> CollectAsync(int count)
    {
        var results = new List<TopicValue>();
        while (results.Count < count)
        {
            var sample = await NextNonTickSampleAsync();
            results.AddRange(sample.Values);
        }
        return results;
    }

    /// <summary>Awaits exactly <paramref name="count"/> non-tick samples (as opposed to <see cref="CollectAsync"/>,
    /// which flattens values across samples) so callers can assert on each sample's own <see cref="Sample.T"/>.</summary>
    public async Task<List<Sample>> CollectSamplesAsync(int count)
    {
        var results = new List<Sample>();
        while (results.Count < count)
            results.Add(await NextNonTickSampleAsync());
        return results;
    }
}
