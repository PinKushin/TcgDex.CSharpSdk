namespace TcgDex.Tests.Http;

using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TcgDex;

/// <summary>
/// The ceiling on how long one request may take.
/// </summary>
/// <remarks>
/// <para>
/// Without this the limit is <see cref="HttpClient"/>'s default of 100 seconds,
/// which nobody chose — a hung endpoint blocks the caller for over a minute and
/// a half. The live API answers its largest endpoint in well under a second, so
/// 30 is roughly forty times the observed worst case and still an order of
/// magnitude below the default it replaces.
/// </para>
/// <para>
/// <b>Applied through a linked <see cref="CancellationTokenSource"/>, not
/// <see cref="HttpClient.Timeout"/>.</b> Callers may supply their own
/// <see cref="HttpClient"/>, which they may share with the rest of their
/// application; mutating its timeout would reach outside this SDK, and
/// <see cref="HttpClient"/> throws outright if a request has already been sent
/// on it. The linked source covers the body read as well as the response
/// headers, which matters because the transport reads headers first and streams
/// the body afterwards.
/// </para>
/// </remarks>
[TestFixture]
public sealed class RequestTimeoutTests
{
    /// <summary>Never answers, until cancelled.</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);

            throw new InvalidOperationException("unreachable: the delay only ends by cancellation");
        }
    }

#if NET8_0_OR_GREATER

    /// <summary>Answers the headers immediately, then stalls on the body.</summary>
    /// <remarks>
    /// <para>
    /// The case a header-only timeout would miss. The transport reads with
    /// <see cref="HttpCompletionOption.ResponseHeadersRead"/>, so a server that
    /// responds and then stops sending is not covered unless the timeout spans
    /// the body too.
    /// </para>
    /// <para>
    /// The cancellable <c>SerializeToStreamAsync</c> overload is deliberate, and
    /// is the assertion: the stall ends only if the transport's deadline reaches
    /// the body read. An earlier version overrode the two-argument overload and
    /// delayed without a token, which no timeout could interrupt — the test hung
    /// indefinitely rather than failing, which is worse than not having it.
    /// </para>
    /// <para>
    /// That overload is .NET 5+, so this pair is scoped to the modern targets.
    /// The behaviour under test is target-independent — it comes from passing
    /// the deadline into <c>BoundedContent</c> — so proving it here is enough.
    /// </para>
    /// </remarks>
    private sealed class StallingBodyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new StallingStream()),
            });

        /// <summary>A body that never produces a byte.</summary>
        /// <remarks>
        /// A stream rather than a custom <see cref="HttpContent"/>, which is
        /// both closer to a real socket and the only version that works.
        /// Stalling inside <c>HttpContent.SerializeToStreamAsync</c> and letting
        /// the deadline cancel it **crashed the test host** — and
        /// <c>dotnet test</c> still printed <c>Passed!</c> for the four tests
        /// that had finished before the abort, which is why this is spelled out
        /// rather than quietly rewritten.
        ///
        /// <see cref="StreamContent"/> hands its stream straight to
        /// <c>ReadAsStreamAsync</c>, so the transport's own
        /// <c>ReadAsync(buffer, deadline)</c> is what observes the expiry —
        /// exactly the call a real response body goes through.
        /// </remarks>
        private sealed class StallingStream : System.IO.Stream
        {
            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override ValueTask<int> ReadAsync(
                Memory<byte> buffer,
                CancellationToken cancellationToken = default)
                => new(Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith(
                    _ => 0,
                    cancellationToken,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default));

            public override Task<int> ReadAsync(
                byte[] buffer,
                int offset,
                int count,
                CancellationToken cancellationToken)
                => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override int Read(byte[] buffer, int offset, int count)
                => throw new NotSupportedException("the transport reads asynchronously");

            public override void Flush()
            {
            }

            public override long Seek(long offset, System.IO.SeekOrigin origin)
                => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count)
                => throw new NotSupportedException();
        }
    }

#endif

    private static TcgDexClient Client(HttpMessageHandler handler, TimeSpan timeout)
        => new(new HttpClient(handler), new TcgDexOptions { Timeout = timeout });

    [Test]
    public void AHangingRequest_FailsWithTheOneErrorType()
    {
        using TcgDexClient client = Client(new HangingHandler(), TimeSpan.FromMilliseconds(200));

        TcgDexApiException error = Should.Throw<TcgDexApiException>(
            () => client.Cards.GetAsync("swsh3-136", CancellationToken.None).GetAwaiter().GetResult());

        error.Message.ShouldContain("timed out", Case.Insensitive);
    }

#if NET8_0_OR_GREATER

    [Test]
    [CancelAfter(15000)]
    public void AStalledBody_AlsoTimesOut()
    {
        // Headers arrive, so anything scoped to SendAsync alone would wait the
        // full HttpClient default here. The NUnit timeout is a backstop: if the
        // deadline ever stops reaching the body read this fails in fifteen
        // seconds instead of hanging the suite, which is how the first version
        // of this test behaved.
        using TcgDexClient client = Client(new StallingBodyHandler(), TimeSpan.FromMilliseconds(200));

        Should.Throw<TcgDexApiException>(
            () => client.Cards.GetAsync("swsh3-136", CancellationToken.None).GetAwaiter().GetResult());
    }

#endif

    [Test]
    public void CallerCancellation_IsNotReportedAsATimeout()
    {
        // The distinction the error contract depends on. A caller who cancels
        // gets OperationCanceledException, which is theirs to observe; only an
        // expiry the SDK imposed becomes a TcgDexApiException.
        using TcgDexClient client = Client(new HangingHandler(), TimeSpan.FromMinutes(5));
        using CancellationTokenSource cancelled = new();

        cancelled.CancelAfter(TimeSpan.FromMilliseconds(100));

        Should.Throw<OperationCanceledException>(
            () => client.Cards.GetAsync("swsh3-136", cancelled.Token).GetAwaiter().GetResult());
    }

    [Test]
    public void AResponseInsideTheBudget_IsUnaffected()
    {
        RecordingHandler handler = new RecordingHandler()
            .RespondWith(HttpStatusCode.OK, Fixture.ReadText("card-pokemon-full.json"));

        using TcgDexClient client = Client(handler, TimeSpan.FromSeconds(30));

        client.Cards.GetAsync("swsh3-136", CancellationToken.None).GetAwaiter().GetResult()
            .ShouldNotBeNull().Name.ShouldBe("Furret");
    }

    [Test]
    public void TheDefault_Is30Seconds()
        => new TcgDexOptions().Timeout.ShouldBe(TimeSpan.FromSeconds(30));

    [Test]
    public void AnInfiniteTimeout_IsAccepted()
        // The documented escape hatch, matching HttpClient's own convention
        // rather than inventing a second one such as zero.
        => Should.NotThrow(new TcgDexOptions { Timeout = Timeout.InfiniteTimeSpan }.Validate);

    [Test]
    public void AnInfiniteTimeout_StillCompletesARequest()
    {
        // Validation accepting the value is not the same as a request working
        // with it. That path skips the CancellationTokenSource entirely, so it
        // is the one branch of the timeout code nothing else reaches — the
        // coverage gate is what pointed it out.
        RecordingHandler handler = new RecordingHandler()
            .RespondWith(HttpStatusCode.OK, Fixture.ReadText("card-pokemon-full.json"));

        using TcgDexClient client = Client(handler, Timeout.InfiniteTimeSpan);

        client.Cards.GetAsync("swsh3-136", CancellationToken.None).GetAwaiter().GetResult()
            .ShouldNotBeNull().Name.ShouldBe("Furret");
    }

    [Test]
    public void AnInfiniteTimeout_StillObservesCallerCancellation()
    {
        // Removing the SDK's deadline must not remove the caller's. With no
        // budget the caller's token is passed through unwrapped, and this is
        // what proves it still arrives.
        using TcgDexClient client = Client(new HangingHandler(), Timeout.InfiniteTimeSpan);
        using CancellationTokenSource cancelled = new();

        cancelled.CancelAfter(TimeSpan.FromMilliseconds(100));

        Should.Throw<OperationCanceledException>(
            () => client.Cards.GetAsync("swsh3-136", cancelled.Token).GetAwaiter().GetResult());
    }

    [TestCase(0)]
    [TestCase(-5)]
    public void ANonPositiveTimeout_IsRejected(int seconds)
    {
        TcgDexOptions options = new() { Timeout = TimeSpan.FromSeconds(seconds) };

        ArgumentException error = Should.Throw<ArgumentException>(options.Validate);

        error.Message.ShouldContain(nameof(TcgDexOptions.Timeout));
        error.Message.ShouldContain("InfiniteTimeSpan");
    }
}
