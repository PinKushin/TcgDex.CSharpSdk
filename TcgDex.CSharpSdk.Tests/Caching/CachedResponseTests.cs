namespace TcgDex.Tests.Caching;

using TcgDex.Caching;

/// <summary>
/// <see cref="CachedResponse.IsFresh"/> at the edge of the window.
/// </summary>
/// <remarks>
/// The handler tests drive freshness with a fake clock, but always well inside or well
/// past the time to live, so <c>&lt;</c> and <c>&lt;=</c> behaved the same there. The
/// edge is the only input that tells them apart: at exactly the time to live an entry
/// has expired, and must be revalidated rather than served.
/// </remarks>
[TestFixture]
public sealed class CachedResponseTests
{
    private static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(5);
    private static readonly CachedResponse Entry = new() { Body = [], StoredAt = UnixEpoch };

    [Test]
    public void AtExactlyTheTimeToLive_IsStale()
        => Entry.IsFresh(UnixEpoch + TimeToLive, TimeToLive).ShouldBeFalse();

    [Test]
    public void OneTickBeforeTheTimeToLive_IsFresh()
        => Entry.IsFresh(UnixEpoch + TimeToLive - TimeSpan.FromTicks(1), TimeToLive).ShouldBeTrue();
}
