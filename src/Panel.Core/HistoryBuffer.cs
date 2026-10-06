namespace Panel.Core;

/// <summary>Time-based ring buffer of samples, pruned by <see cref="Sample.T"/> against a retention window in ticks.</summary>
public sealed class HistoryBuffer
{
    private readonly object _lock = new();
    private readonly LinkedList<Sample> _samples = new();
    private readonly long _retentionTicks;

    public HistoryBuffer(long retentionTicks)
    {
        if (retentionTicks <= 0) throw new ArgumentOutOfRangeException(nameof(retentionTicks));
        _retentionTicks = retentionTicks;
    }

    public void Add(Sample sample)
    {
        lock (_lock)
        {
            _samples.AddLast(sample);
            var cutoff = sample.T - _retentionTicks;
            while (_samples.Count > 0 && _samples.First!.Value.T < cutoff)
                _samples.RemoveFirst();
        }
    }

    /// <summary>Returns a stable snapshot of samples with T &gt;= fromTime, oldest first.</summary>
    public IReadOnlyList<Sample> Since(long fromTime)
    {
        lock (_lock)
        {
            return _samples.Where(s => s.T >= fromTime).ToList();
        }
    }

    public IReadOnlyList<Sample> Snapshot()
    {
        lock (_lock)
        {
            return _samples.ToList();
        }
    }
}
