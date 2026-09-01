namespace TcgDex.Tests.Caching;

using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TcgDex.Caching;

/// <summary>
/// What a caller is told when it is not the one that did the fetching.
/// </summary>
/// <remarks>
/// Coalescing means one caller does the work and the rest wait on its result.
/// That is fine while the leader succeeds. What these pin is the two ways it can
/// go wrong: a waiter being handed the leader's failure as though it were its
/// own, and a waiter losing the ability to stop waiting.
/// </remarks>
[TestFixture]
public sealed class CoalescedWaiterTests
{
    private static readonly Uri Card = new("https://api.tcgdex.net/v2/en/cards/swsh3-136");

    private static (HttpClient Client, CountingHandler Inner) Build()
    {
        CountingHandler inner = new();

        TcgDexCachingHandler caching = new(
            new MemoryTcgDexResponseCache(),
            new TcgDexCacheOptions { CoalesceConcurrentRequests = true })
        {
            InnerHandler = inner,
        };

        return (new HttpClient(caching), inner);
    }

    [Test]
    public async Task AWaiterIsNotHandedTheLeadersFailure()
    {
        // The leader fails; the waiter must get its OWN outcome rather than the
        // leader's exception. Faulting the shared task propagated the leader's
        // cancellation into the waiter's frame, where the transport's filter —
        // which tests the WAITER's token — saw a cancellation the waiter had not
        // requested and reported "the request timed out". A caller that had
        // waited milliseconds and cancelled nothing was told its own request
        // expired, because a different caller on another thread had given up.
        GatedHandler inner = new();

        TcgDexCachingHandler caching = new(
            new MemoryTcgDexResponseCache(),
            new TcgDexCacheOptions { CoalesceConcurrentRequests = true })
        {
            InnerHandler = inner,
        };

        using HttpClient client = new(caching);

        // The leader is held inside the handler, so the second call genuinely
        // becomes a waiter. Two plain calls do NOT: the first completes before
        // the second registers, there is no waiter at all, and the test passes
        // against the defect. That is how the first version of this test was
        // wrong, and the manipulation harness is what said so.
        Task<HttpResponseMessage> leader = client.GetAsync(Card, CancellationToken.None);
        await inner.Arrived.Task;

        Task<HttpResponseMessage> waiter = client.GetAsync(Card, CancellationToken.None);

        // Proof the waiter is waiting rather than fetching: it has not reached
        // the handler. Without this the test could silently go back to having no
        // waiter and would look just as green.
        inner.Requests.ShouldBe(1);

        inner.Release.SetResult(true);

        await Should.ThrowAsync<HttpRequestException>(() => leader);

        // The waiter falls through to its own fetch and succeeds. Faulting the
        // shared task instead handed it the leader's exception — and the
        // transport, filtering on the WAITER's token, reported that as the
        // waiter's own timeout.
        using HttpResponseMessage response = await waiter;

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        inner.Requests.ShouldBe(2);
    }

    /// <summary>
    /// Holds the first request until released, then fails it. Later requests
    /// succeed.
    /// </summary>
    /// <remarks>
    /// Gated rather than delayed, so the test synchronises on the leader having
    /// arrived rather than on a duration.
    /// </remarks>
    private sealed class GatedHandler : HttpMessageHandler
    {
        private int _requests;

        internal TaskCompletionSource<bool> Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int Requests => Volatile.Read(ref _requests);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _requests) == 1)
            {
                Arrived.TrySetResult(true);
                await Release.Task.ConfigureAwait(false);

                throw new HttpRequestException("leader gave up");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"id":"swsh3-136","name":"Furret"}""",
                    System.Text.Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    [Test]
    public async Task AWaiterCanStopWaiting()
    {
        // Awaiting the shared task bare made a waiter's own cancellation
        // unenforceable: it blocked for as long as the leader's fetch took, and
        // with Timeout.InfiniteTimeSpan that is forever. The waiter cancels
        // here while the leader is still in flight, and must observe it.
        (HttpClient client, CountingHandler inner) = Build();

        using (client)
        {
            using CancellationTokenSource waiterToken = new();

            // The leader never completes on its own; the test ends by the
            // waiter's cancellation, not by a clock.
            inner.RespondSlowly(
                HttpStatusCode.OK,
                """{"id":"swsh3-136","name":"Furret"}""",
                etag: null,
                delay: TimeSpan.FromMinutes(5));

            Task<HttpResponseMessage> leader = client.GetAsync(Card, CancellationToken.None);
            Task<HttpResponseMessage> waiter = client.GetAsync(Card, waiterToken.Token);

            waiterToken.Cancel();

            await Should.ThrowAsync<OperationCanceledException>(() => waiter);

            // The leader is deliberately left running: another waiter may still
            // want its result, so one caller giving up must not cancel the fetch
            // for everyone.
            leader.IsCanceled.ShouldBeFalse();
        }
    }
}
