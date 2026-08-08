namespace TcgDex.Tests.Http;

using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TcgDex;

/// <summary>
/// The SDK against response bodies no server would send on purpose.
/// </summary>
/// <remarks>
/// <para>
/// Every other test feeds the parser something valid, or something invalid in a
/// way a human thought of. This one corrupts the recorded fixtures mechanically
/// — truncation, bit flips, byte injection, nesting — and asserts a single
/// property: <b>the SDK either returns a value or throws
/// <see cref="TcgDexApiException"/>, and never anything else.</b>
/// </para>
/// <para>
/// That property is the whole contract. A consumer wraps calls in one catch;
/// an <see cref="IndexOutOfRangeException"/> or a <see cref="NullReferenceException"/>
/// escaping from a malformed body is a crash they cannot reasonably defend
/// against, and it arrives from the network rather than from their own code.
/// </para>
/// <para>
/// <b>This is not fuzzing, and does not replace it.</b> The mutations are
/// deterministic and seeded, so it runs in the normal suite on every push and
/// its failures reproduce exactly. Coverage-guided fuzzing explores paths this
/// cannot reach — see <c>TcgDex.CSharpSdk.Fuzz</c>. The two answer the same
/// question at different depths, and the cheap one is the one that runs often.
/// </para>
/// </remarks>
[TestFixture]
public sealed class MalformedResponseTests
{
    /// <summary>
    /// Fixed so a failure names a reproducible case. A random seed would find
    /// marginally more over time and make every failure a one-off nobody can
    /// re-run, which is the worse trade for a test that gates pushes.
    /// </summary>
    private const int Seed = 20260807;

    private static readonly string[] Fixtures =
    [
        "card-pokemon-full.json",
        "card-damage-string.json",
        "card-energy.json",
        "list-cards-brief.json",
        "set-full.json",
        "error-not-found.json",
    ];

    private static TcgDexClient Client(byte[] body)
        => new(
            new HttpClient(new RawBodyHandler(body)),
            new TcgDexOptions { MaxResponseBytes = 8 * 1024 * 1024 });

    /// <summary>Serves arbitrary bytes as a 200, whatever they are.</summary>
    private sealed class RawBodyHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
            });
    }

    [Test]
    public async Task CorruptedBodies_NeverEscapeTheErrorContract()
    {
        Random random = new(Seed);
        List<string> failures = new();

        foreach (string fixture in Fixtures)
        {
            byte[] original = System.Text.Encoding.UTF8.GetBytes(Fixture.ReadText(fixture));

            foreach ((string? name, byte[]? body) in Corruptions(original, random))
            {
                string? failure = await ObserveAsync(body).ConfigureAwait(false);

                if (failure is not null)
                {
                    failures.Add($"{fixture} / {name}: {failure}");
                }
            }
        }

        failures.ShouldBeEmpty();
    }

    /// <summary>
    /// Runs one body through the client and reports anything that broke the
    /// contract.
    /// </summary>
    /// <param name="body">The response bytes.</param>
    /// <returns><see langword="null"/> when the contract held.</returns>
    private static async Task<string?> ObserveAsync(byte[] body)
    {
        using TcgDexClient client = Client(body);

        try
        {
            await client.Cards.GetAsync("swsh3-136", CancellationToken.None).ConfigureAwait(false);

            return null;
        }
        catch (TcgDexApiException)
        {
            // The documented outcome for a body that cannot be read.
            return null;
        }
        catch (Exception ex)
        {
            // Deliberately broad, and the assertion rather than a swallow: the
            // whole point is to catch the exception types nobody anticipated.
            return $"threw {ex.GetType().Name} — '{ex.Message}'";
        }
    }

    /// <summary>Mechanical corruptions of a valid body.</summary>
    /// <param name="original">The recorded response.</param>
    /// <param name="random">Seeded source for the positional mutations.</param>
    /// <returns>Named corrupt bodies.</returns>
    private static IEnumerable<(string Name, byte[] Body)> Corruptions(byte[] original, Random random)
    {
        yield return ("empty", []);
        yield return ("single brace", "{"u8.ToArray());
        yield return ("bare null", "null"u8.ToArray());

        // Truncation at every tenth of the body. JSON parsers are at their most
        // fragile mid-token, and a socket closing early is the most ordinary
        // real-world corruption there is.
        for (int i = 1; i < 10; i++)
        {
            int cut = original.Length * i / 10;

            // Array.Copy rather than a range expression: System.Index and
            // System.Range do not exist on net472, which this suite also runs.
            byte[] truncated = new byte[cut];
            Array.Copy(original, truncated, cut);

            yield return ($"truncated at {cut}", truncated);
        }

        // Single-byte flips. Enough of them to hit structural characters as well
        // as string contents.
        for (int i = 0; i < 40; i++)
        {
            byte[] copy = (byte[])original.Clone();
            copy[random.Next(copy.Length)] ^= (byte)(1 << random.Next(8));
            yield return ($"bit flip #{i}", copy);
        }

        // Injected structural bytes, which is what turns a valid document into
        // one that is nearly valid — the case a hand-written test never covers.
        foreach (byte injected in new[] { (byte)'{', (byte)'}', (byte)'[', (byte)']', (byte)'"', (byte)0 })
        {
            byte[] copy = (byte[])original.Clone();
            copy[random.Next(copy.Length)] = injected;
            yield return ($"injected '{(char)injected}'", copy);
        }

        // Deep nesting, against the reader's depth limit rather than the models.
        yield return ("2000-deep array", System.Text.Encoding.UTF8.GetBytes(
            new string('[', 2000) + new string(']', 2000)));

        // Valid UTF-8 structure with invalid encoding inside a string.
        yield return ("invalid UTF-8 in a string",
            [.. "{\"name\":\""u8.ToArray(), 0xFF, 0xFE, .. "\"}"u8.ToArray()]);
    }
}
