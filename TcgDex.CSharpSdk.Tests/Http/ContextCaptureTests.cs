namespace TcgDex.Tests.Http;

using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TcgDex;
using TcgDex.Caching;
using TcgDex.Models;
using TcgDex.Querying;
using TcgDex.Tests.Caching;

/// <summary>
/// The SDK never resumes on the caller's <see cref="SynchronizationContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every await in the library ends in <c>ConfigureAwait(false)</c>. Without it,
/// a caller on a UI thread or Unity's main thread has each SDK continuation
/// marshalled back onto that thread, and a caller who blocks on the task there
/// deadlocks. Mutation testing showed none of those calls was pinned: with no
/// context installed, or with every awaited task already complete, true and
/// false behave identically.
/// </para>
/// <para>
/// So each test installs a context that counts what is posted to it, starts the
/// request, and holds one operation open behind a gate so the await before it
/// genuinely suspends. Everything earlier completes synchronously, which keeps
/// the caller's context current right up to the gated await. The gate is then
/// opened from outside the context. A continuation that captured the context
/// shows up as a post.
/// </para>
/// </remarks>
[TestFixture]
public sealed class ContextCaptureTests
{
    private const string CardUrl = "https://api.tcgdex.net/v2/en/cards/swsh3-136";
    private const string CardJson = """{"id":"swsh3-136","name":"Furret"}""";
    private static readonly Uri Card = new(CardUrl);

    /// <summary>Counts posts, and runs them on the pool so nothing waits on this thread.</summary>
    private sealed class CountingContext : SynchronizationContext
    {
        private int _posts;

        internal int Posts => Volatile.Read(ref _posts);

        public override void Post(SendOrPostCallback d, object? state)
        {
            _ = Interlocked.Increment(ref _posts);
            _ = ThreadPool.QueueUserWorkItem(_ => d(state));
        }
    }

    /// <summary>
    /// Whether the counting context was still current when a gated operation started,
    /// for the flow of the test that set it. Per async flow, so parallel tests cannot mix.
    /// </summary>
    private static readonly AsyncLocal<System.Runtime.CompilerServices.StrongBox<bool?>?> ContextAtGate = new();

    private static void NoteContextAtGate()
    {
        // The first gated call only: a stream's later reads come after the SDK
        // has correctly left the caller's context.
        if (ContextAtGate.Value is { Value: null } box)
        {
            box.Value = SynchronizationContext.Current is CountingContext;
        }
    }

    private static async Task<T> AfterAsync<T>(Task gate, Func<Task<T>> next)
    {
        NoteContextAtGate();
        await gate.ConfigureAwait(false);
        return await next().ConfigureAwait(false);
    }

    private static async Task AfterAsync(Task gate, Func<Task> next)
    {
        NoteContextAtGate();
        await gate.ConfigureAwait(false);
        await next().ConfigureAwait(false);
    }

    /// <summary>
    /// Starts <paramref name="start"/> under a counting context, opens the gate
    /// from outside it, and returns the finished work.
    /// </summary>
    private static async Task<Task> AssertNoPostsAsync(
        Func<Task> start,
        TaskCompletionSource<bool> gate,
        bool gateInThisFlow = true)
    {
        CountingContext context = new();
        SynchronizationContext? previous = SynchronizationContext.Current;
        Task work;

        System.Runtime.CompilerServices.StrongBox<bool?> atGate = new(null);
        ContextAtGate.Value = atGate;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            work = start();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
            ContextAtGate.Value = null;
        }

        // The control: if nothing was pending, no await suspended, and a zero
        // below would mean nothing.
        work.IsCompleted.ShouldBeFalse("the gated operation should still be pending");

        gate.SetResult(true);
        _ = await Task.WhenAny(work).ConfigureAwait(false);

        // The second control, read once the work is done so a gated operation
        // reached late has recorded itself: an earlier step that became genuinely
        // async would drop the context before the gated await, and every SDK
        // await after it would pass untested. A coalesced waiter's gate is the
        // leader's, in another flow, so it records nothing here.
        if (gateInThisFlow)
        {
            atGate.Value.ShouldBe(true, "the caller's context was gone before the gated operation started, or the gate was never reached");
        }
        else
        {
            atGate.Value.ShouldBeNull("the waiter reached a gate of its own; it should have waited on the leader");
        }
        context.Posts.ShouldBe(0, "an SDK continuation resumed on the caller's context");
        return work;
    }

    /// <summary>Answers every request, after the gate opens or at once.</summary>
    private sealed class Handler(Func<HttpResponseMessage> respond, bool gated) : HttpMessageHandler
    {
        internal TaskCompletionSource<bool> Gate { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => gated ? AfterAsync(Gate.Task, () => Task.FromResult(respond())) : Task.FromResult(respond());
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json, string? etag = null)
    {
        HttpResponseMessage response = new(status)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };

        if (etag is not null)
        {
            response.Headers.TryAddWithoutValidation("ETag", etag);
        }

        return response;
    }

    /// <summary>A body whose first read waits for the gate.</summary>
    private sealed class GatedStream(byte[] payload, Task gate) : Stream
    {
        private readonly MemoryStream _inner = new(payload);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        // Gated too. .NET Framework buffers an unbounded read through BeginRead,
        // which lands here on a pool thread; ungated, that read could finish
        // before the test's "still pending" control looked, failing it about one
        // run in six on net472.
        public override int Read(byte[] buffer, int offset, int count)
        {
            gate.Wait();
            return _inner.Read(buffer, offset, count);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => AfterAsync(gate, () => Task.FromResult(_inner.Read(buffer, offset, count)));

#if NET8_0_OR_GREATER
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => new(AfterAsync(gate, () => Task.FromResult(_inner.Read(buffer.Span))));
#endif

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Content whose read stream is only handed over once the gate opens.</summary>
    private sealed class GatedStreamContent(byte[] payload, Task gate) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(payload, 0, payload.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
            => AfterAsync(gate, () => Task.FromResult<Stream>(new MemoryStream(payload)));
    }

    /// <summary>A memory cache with one operation held behind the gate.</summary>
    private sealed class GatedCache(ITcgDexResponseCache inner, string gatedOperation) : ITcgDexResponseCache
    {
        internal TaskCompletionSource<bool> Gate { get; } = new();

        public ValueTask<CachedResponse?> GetAsync(string key, CancellationToken cancellationToken = default)
            => gatedOperation == nameof(GetAsync)
                ? new(AfterAsync(Gate.Task, () => inner.GetAsync(key, cancellationToken).AsTask()))
                : inner.GetAsync(key, cancellationToken);

        public ValueTask SetAsync(
            string key,
            CachedResponse response,
            TimeSpan timeToLive,
            CancellationToken cancellationToken = default)
            => gatedOperation == nameof(SetAsync)
                ? new(AfterAsync(Gate.Task, () => inner.SetAsync(key, response, timeToLive, cancellationToken).AsTask()))
                : inner.SetAsync(key, response, timeToLive, cancellationToken);

        public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
            => gatedOperation == nameof(RemoveAsync)
                ? new(AfterAsync(Gate.Task, () => inner.RemoveAsync(key, cancellationToken).AsTask()))
                : inner.RemoveAsync(key, cancellationToken);
    }

    private static HttpClient Caching(
        HttpMessageHandler inner,
        ITcgDexResponseCache cache,
        bool coalesce = true,
        TimeProvider? time = null)
        => new(new TcgDexCachingHandler(cache, new TcgDexCacheOptions { CoalesceConcurrentRequests = coalesce }, time)
        {
            InnerHandler = inner,
        });

    // ----- the transport -----

    [Test]
    public async Task TheTransport_ResumesOffTheCallersContext_AfterTheSend()
    {
        string card = Fixture.ReadText("card-pokemon-full.json");
        Handler handler = new(() => Json(HttpStatusCode.OK, card), gated: true);
        using TcgDexClient client = new(new HttpClient(handler), new TcgDexOptions());

        Task work = await AssertNoPostsAsync(() => client.Cards.GetAsync("swsh3-136", CancellationToken.None), handler.Gate);

        work.Status.ShouldBe(TaskStatus.RanToCompletion);
    }

    [Test]
    public async Task TheTransport_ResumesOffTheCallersContext_ForARequiredResource()
    {
        Handler handler = new(() => Json(HttpStatusCode.OK, """["Common"]"""), gated: true);
        using TcgDexClient client = new(new HttpClient(handler), new TcgDexOptions());

        Task work = await AssertNoPostsAsync(() => client.Catalog.GetRaritiesAsync(CancellationToken.None), handler.Gate);

        work.Status.ShouldBe(TaskStatus.RanToCompletion);
    }

    [Test]
    public async Task TheTransport_ResumesOffTheCallersContext_ReadingAnErrorBody()
    {
        TaskCompletionSource<bool> gate = new();
        byte[] problem = System.Text.Encoding.UTF8.GetBytes(
            """{"type":"https://tcgdex.dev/errors/server","title":"on fire","status":500}""");

        Handler handler = new(
            () => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StreamContent(new GatedStream(problem, gate.Task)),
            },
            gated: false);

        using TcgDexClient client = new(new HttpClient(handler), new TcgDexOptions());

        Task work = await AssertNoPostsAsync(() => client.Cards.GetAsync("swsh3-136", CancellationToken.None), gate);

        work.Exception!.InnerException.ShouldBeOfType<TcgDexApiException>().Message.ShouldContain("on fire");
    }

    // ----- GraphQL and streaming -----

    [Test]
    public async Task GraphQL_ResumesOffTheCallersContext_AfterTheSend()
    {
        Handler handler = new(
            () => Json(
                HttpStatusCode.OK,
                """{"data":{"cards":[{"id":"a","name":"A","category":"Pokemon","localId":"1","set":{"id":"s","name":"S"}}]}}"""),
            gated: true);
        using TcgDexClient client = new(new HttpClient(handler), new TcgDexOptions());

        Task work = await AssertNoPostsAsync(
            () => client.Cards.SearchDetailedAsync(new CardFilter { Name = "A" }, cancellationToken: CancellationToken.None),
            handler.Gate);

        work.Status.ShouldBe(TaskStatus.RanToCompletion);
    }

    [Test]
    public async Task GraphQL_ResumesOffTheCallersContext_AfterReadingTheBody()
    {
        // The send completes at once, so the caller's context is still current at
        // the body read, and only that read is held open.
        TaskCompletionSource<bool> gate = new();
        byte[] payload = System.Text.Encoding.UTF8.GetBytes(
            """{"data":{"cards":[{"id":"a","name":"A","category":"Pokemon","localId":"1","set":{"id":"s","name":"S"}}]}}""");

        Handler handler = new(
            () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new GatedStream(payload, gate.Task)),
            },
            gated: false);

        using TcgDexClient client = new(new HttpClient(handler), new TcgDexOptions());

        Task work = await AssertNoPostsAsync(
            () => client.Cards.SearchDetailedAsync(new CardFilter { Name = "A" }, cancellationToken: CancellationToken.None),
            gate);

        work.Status.ShouldBe(TaskStatus.RanToCompletion);
    }

    [Test]
    public async Task AStream_ResumesOffTheCallersContext_AfterAPage()
    {
        Handler handler = new(() => Json(HttpStatusCode.OK, "[]"), gated: true);
        using TcgDexClient client = new(new HttpClient(handler), new TcgDexOptions());

        // The test's own await must not capture the context either, or it would
        // be the post this test is counting.
        Task work = await AssertNoPostsAsync(
            async () =>
            {
                await foreach (CardBrief _ in client.Cards.StreamAsync(new CardQuery().Page(1, 2), CancellationToken.None).ConfigureAwait(false))
                {
                }
            },
            handler.Gate);

        work.Status.ShouldBe(TaskStatus.RanToCompletion);
    }

    // ----- the bounded body read -----

    [TestCase(1024)]
    [TestCase(0)]
    public async Task ABoundedRead_ResumesOffTheCallersContext_AfterAStreamRead(long maxBytes)
    {
        // A limit reads chunk by chunk; no limit (0) buffers the content whole.
        TaskCompletionSource<bool> gate = new();
        byte[] payload = System.Text.Encoding.UTF8.GetBytes(CardJson);
        using StreamContent content = new(new GatedStream(payload, gate.Task));

        // .NET Framework buffers the unbounded read through StreamContent's
        // BeginRead, which calls the synchronous Read on a pool thread: the read
        // still suspends the SDK's await, but not at this gate.
#if NETFRAMEWORK
        bool gateInThisFlow = maxBytes != 0;
#else
        bool gateInThisFlow = true;
#endif

        Task work = await AssertNoPostsAsync(
            () => BoundedContent.ReadAsBytesAsync(content, maxBytes, Card, CancellationToken.None),
            gate,
            gateInThisFlow);

        ((Task<ArraySegment<byte>>)work).Result.ToArray().ShouldBe(payload);
    }

    [Test]
    public async Task ABoundedRead_ResumesOffTheCallersContext_AfterOpeningTheStream()
    {
        TaskCompletionSource<bool> gate = new();
        byte[] payload = System.Text.Encoding.UTF8.GetBytes(CardJson);
        using GatedStreamContent content = new(payload, gate.Task);

        Task work = await AssertNoPostsAsync(
            () => BoundedContent.ReadAsBytesAsync(content, 1024, Card, CancellationToken.None),
            gate);

        ((Task<ArraySegment<byte>>)work).Result.ToArray().ShouldBe(payload);
    }

    // ----- the caching handler -----

    [TestCase(true)]
    [TestCase(false)]
    public async Task TheCache_ResumesOffTheCallersContext_AfterAFetch(bool coalesce)
    {
        Handler inner = new(() => Json(HttpStatusCode.OK, CardJson, "\"a\""), gated: true);
        using HttpClient client = Caching(inner, new MemoryTcgDexResponseCache(), coalesce);

        Task work = await AssertNoPostsAsync(() => client.GetAsync(Card, CancellationToken.None), inner.Gate);

        ((Task<HttpResponseMessage>)work).Result.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task TheCache_ResumesOffTheCallersContext_PassingANonGetThrough()
    {
        Handler inner = new(() => Json(HttpStatusCode.OK, CardJson), gated: true);
        using HttpClient client = Caching(inner, new MemoryTcgDexResponseCache());
        using StringContent body = new("{}");

        Task work = await AssertNoPostsAsync(() => client.PostAsync(Card, body, CancellationToken.None), inner.Gate);

        ((Task<HttpResponseMessage>)work).Result.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task TheCache_ResumesOffTheCallersContext_WaitingOnALeader()
    {
        Handler inner = new(() => Json(HttpStatusCode.OK, CardJson, "\"a\""), gated: true);
        using HttpClient client = Caching(inner, new MemoryTcgDexResponseCache());

        // The leader starts outside the context; only the waiter is measured.
        Task<HttpResponseMessage> leader = client.GetAsync(Card, CancellationToken.None);

        Task work = await AssertNoPostsAsync(
            () => client.GetAsync(Card, CancellationToken.None),
            inner.Gate,
            gateInThisFlow: false);

        ((Task<HttpResponseMessage>)work).Result.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await leader).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [TestCase(nameof(ITcgDexResponseCache.GetAsync), HttpStatusCode.OK)]
    [TestCase(nameof(ITcgDexResponseCache.SetAsync), HttpStatusCode.OK)]
    [TestCase(nameof(ITcgDexResponseCache.RemoveAsync), HttpStatusCode.InternalServerError)]
    public async Task TheCache_ResumesOffTheCallersContext_AfterACacheCall(string operation, HttpStatusCode status)
    {
        GatedCache cache = new(new MemoryTcgDexResponseCache(), operation);
        Handler inner = new(() => Json(status, CardJson, "\"a\""), gated: false);
        using HttpClient client = Caching(inner, cache);

        Task work = await AssertNoPostsAsync(() => client.GetAsync(Card, CancellationToken.None), cache.Gate);

        ((Task<HttpResponseMessage>)work).Result.StatusCode.ShouldBe(status);
    }

    [Test]
    public async Task TheCache_ResumesOffTheCallersContext_AfterStoringARevalidation()
    {
        FakeTimeProvider time = new();
        MemoryTcgDexResponseCache memory = new(timeProvider: time);
        await memory.SetAsync(
            CardUrl,
            new CachedResponse { Body = System.Text.Encoding.UTF8.GetBytes(CardJson), ETag = "\"a\"", StoredAt = time.GetUtcNow() },
            TimeSpan.FromHours(1));
        time.Advance(TimeSpan.FromMinutes(10));

        GatedCache cache = new(memory, nameof(ITcgDexResponseCache.SetAsync));
        Handler inner = new(() => Json(HttpStatusCode.NotModified, string.Empty, "\"a\""), gated: false);
        using HttpClient client = Caching(inner, cache, time: time);

        Task work = await AssertNoPostsAsync(() => client.GetAsync(Card, CancellationToken.None), cache.Gate);

        HttpResponseMessage response = ((Task<HttpResponseMessage>)work).Result;
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldBe(CardJson);
    }

    [Test]
    public async Task TheCache_ResumesOffTheCallersContext_AfterReadingTheBody()
    {
        TaskCompletionSource<bool> gate = new();
        byte[] payload = System.Text.Encoding.UTF8.GetBytes(CardJson);
        Handler inner = new(
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new GatedStream(payload, gate.Task)) },
            gated: false);
        using HttpClient client = Caching(inner, new MemoryTcgDexResponseCache());

        Task work = await AssertNoPostsAsync(() => client.GetAsync(Card, CancellationToken.None), gate);

        (await ((Task<HttpResponseMessage>)work).Result.Content.ReadAsStringAsync()).ShouldBe(CardJson);
    }
}
