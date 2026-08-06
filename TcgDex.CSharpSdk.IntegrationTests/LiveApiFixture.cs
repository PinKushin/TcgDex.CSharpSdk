namespace TcgDex.IntegrationTests;

/// <summary>
/// Base class for tests that call the real TCGdex API.
/// </summary>
/// <remarks>
/// <para>
/// Every test deriving from this is categorised <c>Integration</c>, so
/// <c>dotnet test --filter TestCategory=Integration</c> selects exactly this
/// set. Documenting that command without applying the attribute would make it
/// silently select nothing.
/// </para>
/// <para>
/// These are deliberately excluded from the per-push CI run: they depend on a
/// third-party service, and a TCGdex outage should not turn a pull request red.
/// They run on a schedule instead, where a failure means the API changed and the
/// SDK needs attention.
/// </para>
/// </remarks>
[Category(Integration)]
public abstract class LiveApiFixture : IDisposable
{
    /// <summary>The NUnit category applied to every live-API test.</summary>
    public const string Integration = "Integration";

    private readonly HttpClient _httpClient = new();

    /// <summary>Creates the fixture and its client.</summary>
    protected LiveApiFixture()
    {
        Client = new TcgDexClient(_httpClient, new TcgDexOptions());
    }

    /// <summary>The client under test, pointed at the live API.</summary>
    protected ITcgDexClient Client { get; }

    /// <summary>
    /// Bounds every request, so a hung call fails the test rather than stalling
    /// the whole run.
    /// </summary>
    protected static CancellationToken Timeout
        => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    /// <summary>Disposes the underlying <see cref="HttpClient"/>.</summary>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Disposes the underlying <see cref="HttpClient"/>.</summary>
    /// <param name="disposing">Whether managed resources should be released.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _httpClient.Dispose();
        }
    }
}
