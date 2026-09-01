namespace TcgDex;

/// <summary>
/// Remembers which endpoints have recently failed, so a dead one is not
/// re-probed by every request.
/// </summary>
/// <remarks>
/// <para>
/// <b>Separate from the handler because it has to outlive it.</b> Under
/// <c>IHttpClientFactory</c> the handler chain is rebuilt on
/// <c>HandlerLifetime</c> — two minutes by default — and a handler that owned
/// this state would forget every failure on that schedule, silently capping a
/// five-minute cooldown at two. One instance is shared by every handler built
/// for a client, so the cooldown means what it says.
/// </para>
/// <para>
/// Entries are read and written with <see cref="Interlocked"/>: several requests
/// run through one handler at once, and on a 32-bit runtime a plain
/// <see cref="long"/> read is not atomic.
/// </para>
/// <para>
/// Two threads can both find an endpoint available and both spend an attempt on
/// it before either records the failure. That race is deliberate rather than
/// guarded: each of those requests was going to contact that endpoint anyway, so
/// the worst case equals the traffic sent with no failover configured at all,
/// and serialising it would put a lock in front of every request to buy nothing.
/// </para>
/// </remarks>
internal sealed class FailoverCooldowns(int endpointCount)
{
    /// <summary>
    /// When each endpoint may be tried again, in UTC ticks. Index 0 is the
    /// primary; the rest follow the configured endpoints.
    /// </summary>
    private readonly long[] _availableAt = new long[endpointCount];

    /// <summary>How many endpoints this tracks, the primary included.</summary>
    internal int Count => _availableAt.Length;

    /// <summary>Whether an endpoint may be tried at the given moment.</summary>
    internal bool IsAvailable(int index, DateTimeOffset now)
        => Interlocked.Read(ref _availableAt[index]) <= now.UtcTicks;

    /// <summary>
    /// Records that an endpoint failed, keeping it out of rotation for the
    /// cooldown. A non-positive cooldown records nothing, which re-tries the
    /// endpoint on the next request.
    /// </summary>
    internal void MarkFailed(int index, DateTimeOffset now, TimeSpan cooldown)
    {
        if (cooldown <= TimeSpan.Zero)
        {
            return;
        }

        Interlocked.Exchange(ref _availableAt[index], now.Add(cooldown).UtcTicks);
    }
}
