namespace TcgDex.Tests.Http;

using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// A test handler that records every request it receives and replays queued
/// responses.
/// </summary>
/// <remarks>
/// <para>
/// Recording the request is the entire point. The previous version of this SDK
/// had a mock handler that ignored its <c>request</c> argument, so nothing in
/// the suite ever asserted a URL — which is how a query parameter the API does
/// not support (<c>?q=</c>) shipped with passing tests.
/// </para>
/// <para>
/// Running out of queued responses fails loudly rather than throwing an opaque
/// "queue empty" from deep inside the pipeline.
/// </para>
/// </remarks>
internal sealed class RecordingHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

    /// <summary>Every request the handler has seen, in order.</summary>
    internal List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>The single request received, failing if there was not exactly one.</summary>
    internal HttpRequestMessage SingleRequest
    {
        get
        {
            Requests.Count.ShouldBe(1, "expected exactly one HTTP request");
            return Requests[0];
        }
    }

    /// <summary>The absolute URI of the single request received.</summary>
    internal string SingleRequestUri => SingleRequest.RequestUri!.ToString();

    internal RecordingHandler RespondWith(HttpStatusCode status, string json)
    {
        _responses.Enqueue(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        });

        return this;
    }

    internal RecordingHandler RespondWithJsonFile(HttpStatusCode status, string fixtureFileName)
        => RespondWith(status, Fixture.ReadText(fixtureFileName));

    internal RecordingHandler RespondWith(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responses.Enqueue(responder);
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Requests.Add(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException(
                $"The SDK made {Requests.Count} request(s) but only " +
                $"{Requests.Count - 1} response(s) were queued. Last request: " +
                $"{request.Method} {request.RequestUri}. Queue another response, " +
                "or fix the code making an unexpected extra call.");
        }

        return Task.FromResult(_responses.Dequeue()(request));
    }
}
