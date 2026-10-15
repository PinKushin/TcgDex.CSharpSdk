namespace TcgDex.Tests.Http;

using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TcgDex;
using TcgDex.Caching;
using TcgDex.Querying;

/// <summary>
/// The failures that arrive after the headers: a body that breaks mid-read or
/// is too large, on a success or an error status, a cancellation that surfaces
/// as an aborted read, and a Content-Length that lies.
/// </summary>
/// <remarks>
/// The transport reads with <see cref="HttpCompletionOption.ResponseHeadersRead"/>,
/// so a connection reset mid-body surfaces from <c>Stream.ReadAsync</c> as an
/// <see cref="IOException"/> rather than the <see cref="HttpRequestException"/>
/// the send path already handled. Each test here pins one way that escaped the
/// single-exception contract.
/// </remarks>
[TestFixture]
public sealed class ExceptionContractTests
{
    /// <summary>Yields one chunk, then fails the way a reset socket does.</summary>
    private sealed class ResettingStream : Stream
    {
        private int _reads;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ++_reads > 1
                ? Task.FromException<int>(new IOException("connection reset by peer"))
                : Task.FromResult(WriteFirstByte(buffer.AsSpan(offset, count)));

#if NET8_0_OR_GREATER
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ++_reads > 1
                ? ValueTask.FromException<int>(new IOException("connection reset by peer"))
                : ValueTask.FromResult(WriteFirstByte(buffer.Span));
#endif

        private static int WriteFirstByte(Span<byte> buffer)
        {
            buffer[0] = (byte)'{';
            return 1;
        }

        // .NET Framework's StreamContent buffers through BeginRead, which lands here.
        public override int Read(byte[] buffer, int offset, int count)
            => ++_reads > 1
                ? throw new IOException("connection reset by peer")
                : WriteFirstByte(buffer.AsSpan(offset, count));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Answers with a body that resets after its first byte.</summary>
    private sealed class ResettingBodyHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StreamContent(new ResettingStream()) });
    }

    // No test for a handler throwing a plain OperationCanceledException at the
    // SDK's deadline: HttpClient converts it to TaskCanceledException on
    // net472, net8.0 and net10.0 alike, with or without a token (verified
    // 2026-10-15 by reverting the transport catch; the test still passed). The
    // catch was widened to OperationCanceledException anyway, which is harmless
    // because TaskCanceledException derives from it.

    private static TcgDexClient Client(HttpMessageHandler handler, TimeSpan? timeout = null)
        => new(new HttpClient(handler), new TcgDexOptions { Timeout = timeout ?? TimeSpan.FromSeconds(30) });

    [Test]
    public void ABodyResetMidRead_IsTheOneErrorType_WithTheIOExceptionInside()
    {
        using TcgDexClient client = Client(new ResettingBodyHandler(HttpStatusCode.OK));

        TcgDexApiException error = Should.Throw<TcgDexApiException>(
            () => client.Cards.GetAsync("swsh3-136", CancellationToken.None).GetAwaiter().GetResult());

        error.InnerException.ShouldBeAssignableTo<IOException>();
    }

    [Test]
    public void ABodyResetMidRead_WithNoSizeLimit_IsTheOneErrorType()
    {
        // The unbounded path buffers through HttpContent, which reports the
        // same reset as HttpRequestException rather than IOException.
        using TcgDexClient client = new(
            new HttpClient(new ResettingBodyHandler(HttpStatusCode.OK)),
            new TcgDexOptions { MaxResponseBytes = 0 });

        Should.Throw<TcgDexApiException>(
            () => client.Cards.GetAsync("swsh3-136", CancellationToken.None).GetAwaiter().GetResult());
    }

    [Test]
    public void AnErrorBodyResetMidRead_StillReportsTheStatusCode()
    {
        // The status is the news on this path, exactly as for an oversized error
        // body: the caller is told about the 500, not about the explanation page
        // that failed to arrive.
        using TcgDexClient client = Client(new ResettingBodyHandler(HttpStatusCode.InternalServerError));

        TcgDexApiException error = Should.Throw<TcgDexApiException>(
            () => client.Cards.GetAsync("swsh3-136", CancellationToken.None).GetAwaiter().GetResult());

        error.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    [Test]
    public async Task AGraphQlErrorBodyResetMidRead_StillReportsTheStatusCode()
    {
        using TcgDexClient client = Client(new ResettingBodyHandler(HttpStatusCode.BadGateway));

        TcgDexApiException error = await Should.ThrowAsync<TcgDexApiException>(
            () => client.Cards.SearchDetailedAsync(
                new CardFilter { Name = "Furret" },
                cancellationToken: CancellationToken.None));

        error.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
    }

    [Test]
    public void ANotFoundWhoseBodyResets_IsNotReportedAsMissing()
    {
        // A 404 is "not found" only once its body proves it is not a language
        // error. A body that never arrived proves nothing, so this must throw
        // rather than return null.
        using TcgDexClient client = Client(new ResettingBodyHandler(HttpStatusCode.NotFound));

        TcgDexApiException error = Should.Throw<TcgDexApiException>(
            () => client.Cards.GetAsync("swsh3-136", CancellationToken.None).GetAwaiter().GetResult());

        error.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        error.InnerException.ShouldBeOfType<TcgDexApiException>();
    }

    [Test]
    public void AnOversizedNotFoundBody_ReportsTheStatus_NotTheLimit()
    {
        // Raising MaxResponseBytes is the wrong advice for an error page: the
        // caller needs to hear about the 404 whose body could not be read.
        using TcgDexClient client = new(
            new HttpClient(new RecordingHandler().RespondWith(HttpStatusCode.NotFound, """{"title":"Not Found"}""")),
            new TcgDexOptions { MaxResponseBytes = 4 });

        TcgDexApiException error = Should.Throw<TcgDexApiException>(
            () => client.Cards.GetAsync("swsh3-136", CancellationToken.None).GetAwaiter().GetResult());

        error.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        error.Message.ShouldContain("404");
        error.Message.ShouldNotContain(nameof(TcgDexOptions.MaxResponseBytes));
        error.InnerException.ShouldBeOfType<TcgDexApiException>();
    }

    /// <summary>Cancels its source, then fails the way an aborted read does.</summary>
    private sealed class CancelThenFailStream(CancellationTokenSource source) : Stream
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

        public override int Read(byte[] buffer, int offset, int count)
        {
            source.Cancel();
            throw new IOException("the read was aborted");
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromResult(Read(buffer, offset, count));

#if NET8_0_OR_GREATER
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Read([], 0, 0));
#endif

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Test]
    public async Task ACancellationSurfacedAsAnIOException_StaysACancellation()
    {
        // A platform may report a cancelled read as an aborted-read IOException.
        // The caller asked to stop, so they must see the cancellation, not a fault.
        using CancellationTokenSource source = new();
        using StreamContent content = new(new CancelThenFailStream(source));

        OperationCanceledException error = await Should.ThrowAsync<OperationCanceledException>(
            () => BoundedContent.ReadAsBytesAsync(
                content,
                1024,
                new Uri("https://api.tcgdex.net/v2/en/cards/a"),
                source.Token));

        error.CancellationToken.ShouldBe(source.Token);
        error.InnerException.ShouldBeAssignableTo<IOException>();
    }

    [Test]
    public async Task ABodyResetMidRead_InsideTheCachingHandler_IsTheOneErrorType()
    {
        TcgDexCachingHandler caching = new(new MemoryTcgDexResponseCache())
        {
            InnerHandler = new ResettingBodyHandler(HttpStatusCode.OK),
        };

        using HttpClient http = new(caching);

        TcgDexApiException error = await Should.ThrowAsync<TcgDexApiException>(
            () => http.GetAsync(new Uri("https://api.tcgdex.net/v2/en/cards/swsh3-136"), CancellationToken.None));

        error.InnerException.ShouldBeAssignableTo<IOException>();
    }

    [Test]
    public async Task AnInflatedContentLength_DoesNotPreallocateIt()
    {
        // 32 MiB declared, 10 bytes sent. Pre-sizing from the claim committed the
        // full 32 MiB per request; the buffer must instead start small and grow.
        byte[] payload = System.Text.Encoding.UTF8.GetBytes("""{"id":"a"}""");
        using StreamContent content = new(new MemoryStream(payload));
        content.Headers.ContentLength = 32L * 1024 * 1024;

        ArraySegment<byte> body = await BoundedContent.ReadAsBytesAsync(
            content,
            32L * 1024 * 1024,
            new Uri("https://api.tcgdex.net/v2/en/cards/a"),
            CancellationToken.None);

        body.ToArray().ShouldBe(payload);
        body.Array!.Length.ShouldBeLessThanOrEqualTo(1024 * 1024);
    }
}
