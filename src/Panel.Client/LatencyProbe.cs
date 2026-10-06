namespace Panel.Client;

/// <summary>One latency observation: when it was received (in the client's <see cref="TimeProvider"/> tick
/// domain) and the measured <c>recv - t_processed</c> delta, in ticks.</summary>
public readonly record struct LatencySample(long RecvTimestamp, long LatencyTicks);

/// <summary>
/// Records <c>recv - t_processed</c> per batch (plan section 7: "Latency probe: records recv - t_processed
/// per batch"). On the same machine, the server's <c>t_processed</c> and the client's
/// <c>TimeProvider.GetTimestamp()</c> share one QPC clock (plan section 2: "comparable across processes on
/// one machine"), so this is a direct subtraction with no offset — see <see cref="TimeSync"/> for the
/// (currently informational) remote-client offset-estimation path.
/// <para>
/// Keeps a bounded ring buffer (default 4096 samples) rather than growing without bound; at the plan's
/// example 20 Hz that is well over three minutes of history, comfortably more than the 10 s window the
/// status bar needs (plan section 8: "live recv - t_processed (p50 and max over 10 s)").
/// </para>
/// </summary>
public sealed class LatencyProbe
{
    private readonly int _capacity;
    private readonly Queue<LatencySample> _samples = new();
    private readonly object _gate = new();

    public LatencyProbe(int capacity = 4096)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    internal void Record(long recvTimestamp, long tProcessed)
    {
        lock (_gate)
        {
            _samples.Enqueue(new LatencySample(recvTimestamp, recvTimestamp - tProcessed));
            while (_samples.Count > _capacity) _samples.Dequeue();
        }
    }

    public IReadOnlyList<LatencySample> Snapshot()
    {
        lock (_gate) return _samples.ToArray();
    }

    /// <summary>p50 and max latency, in ticks, over the samples received within <paramref name="window"/>
    /// of the newest sample. <paramref name="tickFrequency"/> converts the window to ticks (pass
    /// <c>TimeProvider.TimestampFrequency</c>, which equals <c>Stopwatch.Frequency</c> for
    /// <see cref="TimeProvider.System"/>). Returns <c>null</c> when there are no samples yet.</summary>
    public (long P50Ticks, long MaxTicks)? Summary(TimeSpan window, long tickFrequency)
    {
        LatencySample[] snap;
        lock (_gate) snap = _samples.ToArray();
        if (snap.Length == 0) return null;

        var newest = snap[^1].RecvTimestamp;
        var windowTicks = (long)(window.TotalSeconds * tickFrequency);
        var inWindow = snap
            .Where(s => newest - s.RecvTimestamp <= windowTicks)
            .Select(s => s.LatencyTicks)
            .OrderBy(x => x)
            .ToArray();
        if (inWindow.Length == 0) return null;

        var p50 = inWindow[inWindow.Length / 2];
        var max = inWindow[^1];
        return (p50, max);
    }
}
