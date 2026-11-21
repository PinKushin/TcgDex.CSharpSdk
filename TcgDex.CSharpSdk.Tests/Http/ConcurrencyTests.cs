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
/// requests really overlap rather than running one after another.
/// </remarks>
[TestFixture]
public sealed class ConcurrencyTests
{
    private const int Callers = 64;

    /// <summary>Answers every request with the same card, counting the requests.</summary>
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

    private static async Task<Card?[]> CallTogether(TcgDexClient client)
    {
        TaskCompletionSource<bool> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<Card?>[] calls = Enumerable.Range(0, Callers)
            .Select(_ => Task.Run(async () =>
            {
                await gate.Task;
                return await client.Cards.GetAsync("swsh3-136", CancellationToken.None);
            }))
            .ToArray();

        gate.SetResult(true);
        return await Task.WhenAll(calls);
    }

    [Test]
    public async Task OneClient_ManyThreadsAtOnce_EachGetsTheCard()
    {
        CardHandler handler = new(Fixture.ReadText("card-pokemon-full.json"));
        using HttpClient http = new(handler);
        TcgDexClient client = new(http, new TcgDexOptions());

        Card?[] cards = await CallTogether(client);

        cards.ShouldAllBe(c => c != null && c.Id == "swsh3-136");
        handler.Requests.ShouldBe(Callers, "without a cache every call is its own request");
    }

    [Test]
    public async Task OneCachingClient_ManyThreadsAtOnce_EachGetsTheCard()
    {
        CardHandler handler = new(Fixture.ReadText("card-pokemon-full.json"));
        using HttpClient http = new(new TcgDexCachingHandler(new MemoryTcgDexResponseCache()) { InnerHandler = handler });
        TcgDexClient client = new(http, new TcgDexOptions());

        Card?[] cards = await CallTogether(client);

        cards.ShouldAllBe(c => c != null && c.Id == "swsh3-136");
        handler.Requests.ShouldBeInRange(1, Callers);
    }
}
