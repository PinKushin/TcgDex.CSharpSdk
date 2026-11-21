namespace TcgDex.Tests.Http;

using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TcgDex;
using TcgDex.Caching;
using TcgDex.Models;

/// <summary>
/// One client, called from many threads at once: what the <see cref="TcgDexClient"/>
/// remarks promise (DECISIONS #37, row 17d).
/// </summary>
/// <remarks>
/// Every call starts on its own thread-pool thread and waits on one gate, so the
/// requests overlap rather than running one after another.
/// </remarks>
[TestFixture]
public sealed class ConcurrencyTests
{
    private const int Callers = 64;

    /// <summary>Answers every request with the same card and an ETag, counting the requests.</summary>
    private sealed class CardHandler(string json) : HttpMessageHandler
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
            return Task.FromResult(response);
        }
    }

    private static async Task<Card?[]> CallTogether(TcgDexClient client, Func<int, string> id)
    {
        TaskCompletionSource<bool> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<Card?>[] calls = Enumerable.Range(0, Callers)
            .Select(i => Task.Run(async () =>
            {
                await gate.Task;
                return await client.Cards.GetAsync(id(i), CancellationToken.None);
            }))
            .ToArray();

        gate.SetResult(true);
        return await Task.WhenAll(calls);
    }

    [Test]
    public async Task OneClient_ManyThreadsAtOnce_EachGetsTheCard_InItsOwnRequest()
    {
        CardHandler handler = new(Fixture.ReadText("card-pokemon-full.json"));
        using HttpClient http = new(handler);
        TcgDexClient client = new(http, new TcgDexOptions());

        Card?[] cards = await CallTogether(client, _ => "swsh3-136");

        cards.ShouldAllBe(c => c != null && c.Id == "swsh3-136");
        handler.Requests.ShouldBe(Callers, "without a cache every call is its own request");
    }

    [Test]
    public async Task OneCachingClient_ManyDistinctCardsAtOnce_EachGetsItsCard_AndTheCacheStaysBounded()
    {
        // 64 keys into a cache of 8, so eviction runs while the requests overlap. How far they overlap is up to
        // the thread pool, so this is a smoke test of the client path; BoundedLruCountTests, on dedicated
        // threads, is the one that pins the eviction bound.
        CardHandler handler = new(Fixture.ReadText("card-pokemon-full.json"));
        MemoryTcgDexResponseCache cache = new(maxEntries: 8);
        using HttpClient http = new(new TcgDexCachingHandler(cache) { InnerHandler = handler });
        TcgDexClient client = new(http, new TcgDexOptions());

        Card?[] cards = await CallTogether(client, i => $"card-{i}");

        cards.ShouldAllBe(c => c != null && c.Id == "swsh3-136");
        handler.Requests.ShouldBe(Callers, "every key is distinct, so none is shared");
        cache.Count.ShouldBeLessThanOrEqualTo(8);
    }

    [Test]
    public async Task OneWarmCachingClient_ManyThreadsAtOnce_AreAllServedFromTheCache()
    {
        CardHandler handler = new(Fixture.ReadText("card-pokemon-full.json"));
        TcgDexCacheOptions fresh = new() { DefaultTimeToLive = TimeSpan.FromHours(1) };
        using HttpClient http = new(new TcgDexCachingHandler(new MemoryTcgDexResponseCache(), fresh) { InnerHandler = handler });
        TcgDexClient client = new(http, new TcgDexOptions());

        _ = await client.Cards.GetAsync("swsh3-136", CancellationToken.None);
        handler.Requests.ShouldBe(1);

        Card?[] cards = await CallTogether(client, _ => "swsh3-136");

        cards.ShouldAllBe(c => c != null && c.Id == "swsh3-136");
        handler.Requests.ShouldBe(1, "a fresh entry answers every concurrent hit without a request");
    }
}
