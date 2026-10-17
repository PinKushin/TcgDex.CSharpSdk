namespace TcgDex.Tests.Caching;

using System.Threading.Tasks;
using TcgDex.Caching;

/// <summary>
/// A time-to-live long enough that the retention arithmetic leaves its range.
/// </summary>
/// <remarks>
/// An entry is retained for twelve times its time-to-live. Twelve times a huge
/// span overflowed <see cref="long"/> ticks and wrapped negative, and adding a
/// span that fits to "now" could pass <see cref="DateTimeOffset.MaxValue"/> and
/// throw. "Effectively forever" is a legitimate thing to ask a cache for, so
/// both are clamped rather than rejected.
/// </remarks>
[TestFixture]
public sealed class MemoryCacheLifetimeTests
{
    private static CachedResponse Sample() => new()
    {
        Body = System.Text.Encoding.UTF8.GetBytes("{}"),
        StoredAt = UnixEpoch,
    };

    [Test]
    public async Task MaxValueTimeToLive_IsRetainedRatherThanWrappingIntoThePast()
    {
        MemoryTcgDexResponseCache cache = new(timeProvider: new FakeTimeProvider());

        await cache.SetAsync("k", Sample(), TimeSpan.MaxValue);

        (await cache.GetAsync("k")).ShouldNotBeNull();
    }

    [Test]
    public async Task TimeToLivePastTheCalendar_DoesNotThrow()
    {
        // Twelve times this fits in a TimeSpan, so the multiplication is fine;
        // it is "now + lifetime" that leaves DateTimeOffset's range.
        MemoryTcgDexResponseCache cache = new(timeProvider: new FakeTimeProvider());

        await cache.SetAsync("k", Sample(), TimeSpan.FromDays(365.0 * 1000));

        (await cache.GetAsync("k")).ShouldNotBeNull();
    }
}
