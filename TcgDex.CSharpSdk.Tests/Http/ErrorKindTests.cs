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
/// Every <see cref="TcgDexApiException"/> says what failed and which request.
/// </summary>
/// <remarks>
/// One test per throw site, REST and GraphQL separately: they build their
/// exceptions in different places, so a kind set on one is no evidence for the
/// other. Each asserts the exact kind and the exact URI, so a site left on
/// <see cref="TcgDexErrorKind.Unspecified"/> or reporting the wrong request fails.
/// </remarks>
[TestFixture]
public sealed class ErrorKindTests
{
    private const string CardUri = "https://api.tcgdex.net/v2/en/cards/swsh3-136";
    private const string GraphQLUri = "https://api.tcgdex.net/v2/graphql";

    private sealed class LambdaHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request, cancellationToken);
    }

    /// <summary>A body that fails on its first read, like a reset socket.</summary>
    private sealed class ResetStream : Stream
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

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("connection reset");

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromException<int>(new IOException("connection reset"));

#if NET8_0_OR_GREATER
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(new IOException("connection reset"));
#endif

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Answer(
        HttpStatusCode status,
        string body)
        => (_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        });

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Throw(Exception exception)
        => (_, _) => Task.FromException<HttpResponseMessage>(exception);

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> ResetBody()
        => (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ResetStream()),
        });

    /// <summary>Never answers; ends only when the token it was sent with is cancelled.</summary>
    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Hang()
        => async (_, token) =>
        {
            TaskCompletionSource<HttpResponseMessage> never = new(TaskCreationOptions.RunContinuationsAsynchronously);

            using (token.Register(() => never.TrySetCanceled(token)))
            {
                return await never.Task;
            }
        };

    private static TcgDexClient Client(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
        Action<TcgDexOptions>? configure = null)
    {
        TcgDexOptions options = new();
        configure?.Invoke(options);

        return new TcgDexClient(new HttpClient(new LambdaHandler(respond)), options);
    }

    private static async Task<TcgDexApiException> RestFailure(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
        Action<TcgDexOptions>? configure = null)
        => await Should.ThrowAsync<TcgDexApiException>(
            () => Client(respond, configure).Cards.GetAsync("swsh3-136", CancellationToken.None));

    private static async Task<TcgDexApiException> GraphQLFailure(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
        Action<TcgDexOptions>? configure = null)
        => await Should.ThrowAsync<TcgDexApiException>(
            () => Client(respond, configure).Cards.SearchDetailedAsync(
                new CardFilter { Name = "Furret" },
                cancellationToken: CancellationToken.None));

    private static void ShouldBe(TcgDexApiException error, TcgDexErrorKind kind, string uri)
    {
        error.Kind.ShouldBe(kind);
        error.RequestUri.ShouldBe(new Uri(uri));
    }

    // ----- REST -----

    [Test]
    public async Task Rest_ErrorStatus_IsHttpStatus()
    {
        TcgDexApiException error = await RestFailure(Answer(HttpStatusCode.InternalServerError, string.Empty));

        ShouldBe(error, TcgDexErrorKind.HttpStatus, CardUri);
        error.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    [Test]
    public async Task Rest_LanguageNotFound_IsHttpStatusWithLanguageError()
    {
        TcgDexApiException error = await RestFailure(Answer(
            HttpStatusCode.NotFound,
            """{"type":"https://tcgdex.dev/errors/language-invalid","title":"x","status":404}"""));

        ShouldBe(error, TcgDexErrorKind.HttpStatus, CardUri);
        error.IsLanguageError.ShouldBeTrue();
    }

    [Test]
    public async Task Rest_SendFailure_IsNetwork()
        => ShouldBe(await RestFailure(Throw(new HttpRequestException("refused"))), TcgDexErrorKind.Network, CardUri);

    [Test]
    public async Task Rest_BodyReset_IsNetwork()
        => ShouldBe(await RestFailure(ResetBody()), TcgDexErrorKind.Network, CardUri);

    [Test]
    public async Task Rest_Budget_IsTimeout()
        => ShouldBe(
            await RestFailure(Hang(), o => o.Timeout = TimeSpan.FromMilliseconds(20)),
            TcgDexErrorKind.Timeout,
            CardUri);

    [Test]
    public async Task Rest_PipelineCancellation_IsCanceled()
        => ShouldBe(
            await RestFailure(Throw(new TaskCanceledException("HttpClient.Timeout"))),
            TcgDexErrorKind.Canceled,
            CardUri);

    [Test]
    public async Task Rest_OversizedBody_IsResponseTooLarge()
        => ShouldBe(
            await RestFailure(Answer(HttpStatusCode.OK, "{}"), o => o.MaxResponseBytes = 1),
            TcgDexErrorKind.ResponseTooLarge,
            CardUri);

    [Test]
    public async Task Rest_MalformedJson_IsInvalidResponse()
    {
        TcgDexApiException error = await RestFailure(Answer(HttpStatusCode.OK, "<html>"));

        ShouldBe(error, TcgDexErrorKind.InvalidResponse, CardUri);
        error.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task Rest_RequiredEndpointMissing_IsInvalidResponse()
    {
        TcgDexApiException error = await Should.ThrowAsync<TcgDexApiException>(
            () => Client(Answer(HttpStatusCode.NotFound, string.Empty)).Catalog.GetRaritiesAsync(CancellationToken.None));

        ShouldBe(error, TcgDexErrorKind.InvalidResponse, "https://api.tcgdex.net/v2/en/rarities");
    }

    // ----- GraphQL -----

    [Test]
    public async Task GraphQL_ErrorsArray_IsGraphQLError()
        => ShouldBe(
            await GraphQLFailure(Answer(HttpStatusCode.OK, """{"errors":[{"message":"bad"}]}""")),
            TcgDexErrorKind.GraphQLError,
            GraphQLUri);

    [Test]
    public async Task GraphQL_ErrorStatus_IsHttpStatus()
    {
        TcgDexApiException error = await GraphQLFailure(Answer(HttpStatusCode.BadGateway, string.Empty));

        ShouldBe(error, TcgDexErrorKind.HttpStatus, GraphQLUri);
        error.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
    }

    [Test]
    public async Task GraphQL_SendFailure_IsNetwork()
        => ShouldBe(await GraphQLFailure(Throw(new HttpRequestException("refused"))), TcgDexErrorKind.Network, GraphQLUri);

    [Test]
    public async Task GraphQL_BodyReset_IsNetwork()
        => ShouldBe(await GraphQLFailure(ResetBody()), TcgDexErrorKind.Network, GraphQLUri);

    [Test]
    public async Task GraphQL_Budget_IsTimeout()
        => ShouldBe(
            await GraphQLFailure(Hang(), o => o.Timeout = TimeSpan.FromMilliseconds(20)),
            TcgDexErrorKind.Timeout,
            GraphQLUri);

    [Test]
    public async Task GraphQL_PipelineCancellation_IsCanceled()
        => ShouldBe(
            await GraphQLFailure(Throw(new TaskCanceledException("HttpClient.Timeout"))),
            TcgDexErrorKind.Canceled,
            GraphQLUri);

    [Test]
    public async Task GraphQL_OversizedBody_IsResponseTooLarge()
        => ShouldBe(
            await GraphQLFailure(Answer(HttpStatusCode.OK, "{}"), o => o.MaxResponseBytes = 1),
            TcgDexErrorKind.ResponseTooLarge,
            GraphQLUri);

    [Test]
    public async Task GraphQL_MalformedJson_IsInvalidResponse()
        => ShouldBe(await GraphQLFailure(Answer(HttpStatusCode.OK, "<html>")), TcgDexErrorKind.InvalidResponse, GraphQLUri);

    [Test]
    public async Task GraphQL_NullDocument_IsInvalidResponse()
        => ShouldBe(await GraphQLFailure(Answer(HttpStatusCode.OK, "null")), TcgDexErrorKind.InvalidResponse, GraphQLUri);

    // ----- the caching handler, used directly -----

    [Test]
    public async Task CachingHandler_OversizedBody_IsResponseTooLarge()
    {
        using HttpClient http = new(new TcgDexCachingHandler(new MemoryTcgDexResponseCache(), maxResponseBytes: 1)
        {
            InnerHandler = new LambdaHandler(Answer(HttpStatusCode.OK, "{}")),
        });

        TcgDexApiException error = await Should.ThrowAsync<TcgDexApiException>(() => http.GetAsync(new Uri(CardUri)));

        ShouldBe(error, TcgDexErrorKind.ResponseTooLarge, CardUri);
    }

    [Test]
    public async Task CachingHandler_BodyReset_IsNetwork()
    {
        using HttpClient http = new(new TcgDexCachingHandler(new MemoryTcgDexResponseCache())
        {
            InnerHandler = new LambdaHandler(ResetBody()),
        });

        TcgDexApiException error = await Should.ThrowAsync<TcgDexApiException>(() => http.GetAsync(new Uri(CardUri)));

        ShouldBe(error, TcgDexErrorKind.Network, CardUri);
    }
}
