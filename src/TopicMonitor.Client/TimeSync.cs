using TopicMonitor.Contracts;

namespace TopicMonitor.Client;

/// <summary>
/// A single-sample round-trip estimate of the offset between the client's <see cref="TimeProvider"/> tick
/// domain and the server's mono clock, obtained via <c>GetTime()</c>.
/// <para>
/// On one machine the two domains are identical (offset ~ 0 modulo scheduling noise) and this estimate
/// isn't needed for the latency probe (see <see cref="LatencyProbe"/>); it is wired up 
/// ("GetTime() offset estimation is wired in for remote clients") so the path exists for v2. The estimator
/// is deliberately simple: assume the request/response latency split evenly around the moment the server
/// reports its timestamp.
/// </para>
/// </summary>
public readonly record struct TimeOffsetEstimate(long OffsetTicks, long RoundTripTicks, long ServerMonoFrequency)
{
    /// <summary>Converts a timestamp from the client's tick domain into the server's tick domain.</summary>
    public long ToServerTicks(long clientTimestamp) => clientTimestamp + OffsetTicks;
}

public static class TimeSync
{
    /// <param name="clientSendTimestamp">Client <see cref="TimeProvider.GetTimestamp"/> just before calling GetTime().</param>
    /// <param name="clientRecvTimestamp">Client <see cref="TimeProvider.GetTimestamp"/> just after GetTime() returned.</param>
    /// <param name="reply">The server's <c>GetTime()</c> response.</param>
    public static TimeOffsetEstimate Estimate(long clientSendTimestamp, long clientRecvTimestamp, TimeReply reply)
    {
        var rtt = clientRecvTimestamp - clientSendTimestamp;
        var clientMidpoint = clientSendTimestamp + rtt / 2;
        var offset = reply.ServerMono - clientMidpoint;
        return new TimeOffsetEstimate(offset, rtt, reply.MonoFrequency);
    }
}
