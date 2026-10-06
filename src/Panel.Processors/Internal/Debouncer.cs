namespace Panel.Processors.Internal;

/// <summary>
/// Shared k-sample debounce used by the color classifier and the text stabilizer (plan section 5:
/// "new color/text after k equal samples", default k = 2).
///
/// Rule implemented:
/// - The very first observation ever is adopted immediately as the confirmed value (there is nothing
///   to debounce against yet, so there is no artificial startup delay).
/// - A value equal to the current confirmed value is a no-op; it also cancels any in-progress candidate
///   run, so a single-sample glitch (confirmed -> X -> confirmed) never flips the output.
/// - A value that differs from the confirmed value starts or continues a "candidate" run. Once the same
///   candidate has been observed k times in a row, it becomes the new confirmed value.
/// - The candidate run's first sample is remembered so callers can stamp EvidenceSince with it (plan
///   section 5: "EvidenceSince points at the first supporting sample"), while the sample that completes
///   the run (the k-th, i.e. the latest one) is the "triggering sample" whose time stamps the output.
/// </summary>
internal sealed class Debouncer<T> where T : class
{
    private readonly int _k;

    private bool _hasConfirmed;
    private T? _confirmed;

    private T? _candidate;
    private int _candidateCount;
    private long _candidateFirstT;

    public Debouncer(int k)
    {
        if (k < 1) throw new ArgumentOutOfRangeException(nameof(k), "k must be >= 1.");
        _k = k;
    }

    public bool HasConfirmed => _hasConfirmed;

    public T? Confirmed => _confirmed;

    /// <summary>
    /// Feeds one observation at time <paramref name="t"/>. Returns true if this observation newly
    /// establishes or changes the confirmed value (i.e. the caller should publish), in which case
    /// <paramref name="evidenceSince"/> is the time of the first supporting sample. Returns false
    /// (evidenceSince is meaningless) when nothing changed.
    /// </summary>
    public bool Observe(T value, long t, out long evidenceSince)
    {
        evidenceSince = t;

        if (!_hasConfirmed)
        {
            _confirmed = value;
            _hasConfirmed = true;
            _candidate = null;
            _candidateCount = 0;
            return true;
        }

        if (Equals(value, _confirmed))
        {
            // Back to the established value: cancel any in-progress candidate (glitch recovered).
            _candidate = null;
            _candidateCount = 0;
            return false;
        }

        if (_candidateCount > 0 && Equals(value, _candidate))
        {
            _candidateCount++;
        }
        else
        {
            _candidate = value;
            _candidateCount = 1;
            _candidateFirstT = t;
        }

        if (_candidateCount >= _k)
        {
            _confirmed = _candidate;
            evidenceSince = _candidateFirstT;
            _candidate = null;
            _candidateCount = 0;
            return true;
        }

        return false;
    }

    /// <summary>Clears all state, including the confirmed value. Used when upstream data becomes Invalid:
    /// the next valid sample re-bootstraps (confirmed immediately) rather than trusting stale state across
    /// an unknown gap.</summary>
    public void Reset()
    {
        _hasConfirmed = false;
        _confirmed = null;
        _candidate = null;
        _candidateCount = 0;
    }

    private static bool Equals(T? a, T? b) => EqualityComparer<T>.Default.Equals(a!, b!);
}
