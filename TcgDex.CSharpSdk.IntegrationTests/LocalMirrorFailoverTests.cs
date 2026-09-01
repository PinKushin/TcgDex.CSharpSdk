namespace TcgDex.IntegrationTests;

using System.Net;
using System.Net.Sockets;
using System.Text;
using TcgDex;

/// <summary>
/// Failover over a real socket, against servers running on loopback.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately not <c>[Category("Integration")]</c>, and that is the point.</b>
/// These open real TCP connections but only to 127.0.0.1, so they depend on no
/// external service and cannot be broken by TCGdex having a bad day. CI runs the
/// integration project with <c>TestCategory!=Integration</c>, so these gate every
/// pull request alongside the unit suite.
/// </para>
/// <para>
/// They exist because the unit tests for this handler terminate at an in-process
/// <c>HttpMessageHandler</c> — which is what makes them hermetic, and also what
/// makes them unable to observe two things that only exist further down: a
/// genuine refused connection, and the real handler stack that
/// <see cref="TcgDexClient.Create"/> builds. A stub can return an exception that
/// resembles a connection failure; only a closed port produces one.
/// </para>
/// <para>
/// The catalogue endpoint is used as the payload because it is a bare JSON array
/// of strings — enough to prove the response travelled the whole path and
/// deserialized, without tying the test to the shape of a card.
/// </para>
/// </remarks>
[TestFixture]
public sealed class LocalMirrorFailoverTests
{
    /// <summary>
    /// A catalogue payload naming the server that produced it.
    /// </summary>
    /// <remarks>
    /// Each mirror serves a payload only it can serve, so the value returned to
    /// the caller identifies which server answered. Serving identical bodies
    /// would leave "the mirror answered" and "something answered" independently
    /// unprovable from the data, resting the whole claim on a request counter.
    /// </remarks>
    private static string ServedBy(string name) => $"""["served-by-{name}"]""";

    [Test]
    public async Task AnUnreachablePrimary_FailsOverToAMirror()
    {
        // A port with nothing behind it: the transport really does refuse, which
        // is the case a stubbed handler can only imitate.
        using LocalMirror mirror = new(200, ServedBy("mirror"));
        Uri unreachable = new($"http://127.0.0.1:{ClosedPort()}/v2/");

        TcgDexOptions options = new() { BaseAddress = unreachable };
        options.UseFailover(mirror.BaseAddress);

        using TcgDexClient client = TcgDexClient.Create(options);

        IReadOnlyList<string> rarities = await client.Catalog.RaritiesAsync(CancellationToken.None);

        // The data came out of that server's socket, and that server counted the
        // connection. Two independent witnesses that the fallback was not merely
        // selected but actually reached.
        rarities.ShouldBe(["served-by-mirror"]);
        mirror.Requests.ShouldBe(1);
    }

    [Test]
    public async Task AGatewayErrorFromARealServer_FailsOverToAMirror()
    {
        // The documented TCGdex outage shape — a crashed container answering 502
        // with an HTML body rather than problem-details JSON.
        using LocalMirror broken = new(502, "<html><body>Bad Gateway</body></html>");
        using LocalMirror mirror = new(200, ServedBy("mirror"));

        TcgDexOptions options = new() { BaseAddress = broken.BaseAddress };
        options.UseFailover(mirror.BaseAddress);

        using TcgDexClient client = TcgDexClient.Create(options);

        IReadOnlyList<string> rarities = await client.Catalog.RaritiesAsync(CancellationToken.None);

        rarities.ShouldBe(["served-by-mirror"]);

        // Both were really contacted: one to fail, one to serve. Asserting only
        // the result would pass against a client that never tried the primary.
        broken.Requests.ShouldBe(1);
        mirror.Requests.ShouldBe(1);
    }

    [Test]
    public async Task TheFirstReachableMirror_ServesTheRequest()
    {
        // Ordering, which a single fallback cannot show. With two live mirrors
        // serving distinguishable payloads, the returned value says which one
        // answered — so a handler that rotated to the wrong endpoint, or tried
        // them in the wrong order, fails here rather than passing on "some
        // mirror answered".
        using LocalMirror broken = new(502, "<html><body>Bad Gateway</body></html>");
        using LocalMirror first = new(200, ServedBy("first"));
        using LocalMirror second = new(200, ServedBy("second"));

        TcgDexOptions options = new() { BaseAddress = broken.BaseAddress };
        options.UseFailover(first.BaseAddress, second.BaseAddress);

        using TcgDexClient client = TcgDexClient.Create(options);

        IReadOnlyList<string> rarities = await client.Catalog.RaritiesAsync(CancellationToken.None);

        rarities.ShouldBe(["served-by-first"]);

        broken.Requests.ShouldBe(1);
        first.Requests.ShouldBe(1);

        // The control: rotation stops at the first endpoint that can serve, so
        // the second is never contacted at all.
        second.Requests.ShouldBe(0);
    }

    [Test]
    public async Task WithoutFailover_AnUnreachableHostStillThrows()
    {
        // The control. Every assertion above is about failover being configured;
        // this one confirms the same unreachable host is still a failure without
        // it, so the tests above are measuring the feature rather than some
        // incidental resilience in the transport.
        using LocalMirror mirror = new(200, """["Common","Rare"]""");
        Uri unreachable = new($"http://127.0.0.1:{ClosedPort()}/v2/");

        using TcgDexClient client = TcgDexClient.Create(new TcgDexOptions
        {
            BaseAddress = unreachable,
        });

        await Should.ThrowAsync<TcgDexApiException>(
            () => client.Catalog.RaritiesAsync(CancellationToken.None));

        mirror.Requests.ShouldBe(0);
    }

    /// <summary>
    /// A loopback port with nothing listening on it.
    /// </summary>
    /// <remarks>
    /// Bound and released to have the OS name a port that was free a moment ago.
    /// Something else could claim it in between; that would fail this test
    /// loudly rather than passing it quietly, which is the acceptable direction.
    /// </remarks>
    private static int ClosedPort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        return port;
    }

    /// <summary>
    /// A minimal HTTP/1.1 server on loopback, serving one canned response.
    /// </summary>
    /// <remarks>
    /// Hand-rolled over <see cref="TcpListener"/> rather than hosted on Kestrel:
    /// the whole job is to answer with a status and a body, and a web framework
    /// would add a dependency and a startup cost to the test project for
    /// something a request line and a response header can do.
    /// </remarks>
    private sealed class LocalMirror : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stopping = new();
        private readonly int _status;
        private readonly string _body;
        private int _requests;

        internal LocalMirror(int status, string body)
        {
            _status = status;
            _body = body;

            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();

            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

            _ = Task.Run(AcceptAsync);
        }

        internal int Port { get; }

        /// <summary>The API root this mirror serves, shaped like the real one.</summary>
        internal Uri BaseAddress => new($"http://127.0.0.1:{Port}/v2/");

        /// <summary>How many requests actually arrived.</summary>
        internal int Requests => Volatile.Read(ref _requests);

        public void Dispose()
        {
            _stopping.Cancel();
            _listener.Stop();
            _stopping.Dispose();
        }

        private async Task AcceptAsync()
        {
            while (!_stopping.IsCancellationRequested)
            {
                TcpClient connection;

                try
                {
                    connection = await _listener.AcceptTcpClientAsync(_stopping.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (SocketException)
                {
                    // The listener was stopped while an accept was pending.
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                _ = Task.Run(() => ServeAsync(connection));
            }
        }

        private async Task ServeAsync(TcpClient connection)
        {
            using (connection)
            {
                NetworkStream stream = connection.GetStream();

                // Read to the end of the headers. The body is irrelevant — this
                // handler only ever retries GET, which has none.
                byte[] buffer = new byte[8192];
                int total = 0;

                while (total < buffer.Length)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total));

                    if (read == 0)
                    {
                        break;
                    }

                    total += read;

                    if (Encoding.ASCII.GetString(buffer, 0, total).Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        break;
                    }
                }

                Interlocked.Increment(ref _requests);

                byte[] payload = Encoding.UTF8.GetBytes(_body);

                // Connection: close keeps this honest — every request opens its
                // own connection, so the request count is a count of requests
                // rather than of keep-alive reuse.
                string head =
                    $"HTTP/1.1 {_status} Test\r\n" +
                    "Content-Type: application/json\r\n" +
                    $"Content-Length: {payload.Length}\r\n" +
                    "Connection: close\r\n\r\n";

                await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                await stream.WriteAsync(payload);
                await stream.FlushAsync();
            }
        }
    }
}
