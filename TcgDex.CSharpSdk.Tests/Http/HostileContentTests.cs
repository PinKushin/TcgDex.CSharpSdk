namespace TcgDex.Tests.Http;

using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TcgDex;

/// <summary>
/// Valid JSON, hostile <em>content</em>. Where <see cref="MalformedResponseTests"/>
/// corrupts bytes, this sends well-formed JSON whose shape is wrong in the ways a
/// real API actually misbehaves — a field of the wrong type, a null where a value
/// is required, a number too large for the type behind it.
/// </summary>
/// <remarks>
/// <para>
/// The contract under test is the one the transport promises: a body it cannot map
/// to the model surfaces as <see cref="TcgDexApiException"/> and nothing else. A
/// caller catches one type. So the failure this hunts is not "it threw" — it is "it
/// threw something OTHER than <see cref="TcgDexApiException"/>", which means an
/// implementation exception leaked past the wrapper.
/// </para>
/// <para>
/// This exists because line coverage does not measure inputs. The suite was already
/// at 99%+ line and 88% mutation when a real card with a null attack name
/// (<c>2017sm-5</c>) slipped through untested — coverage said the deserialize path
/// ran, but no case had ever sent that shape. Custom converters are the sharp edge:
/// <see cref="Serialization.FlexibleStringConverter"/> falls back to
/// <c>GetDecimal()</c>, which throws <c>FormatException</c>, not <c>JsonException</c>,
/// on a number too large for <see cref="decimal"/> — a leak the wrapper's
/// <c>catch (JsonException)</c> would not catch.
/// </para>
/// </remarks>
[TestFixture]
public sealed class HostileContentTests
{
    /// <summary>Serves the given UTF-8 string as a 200, whatever it is.</summary>
    private sealed class RawBodyHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body),
            });
    }

    private static TcgDexClient Client(string body)
        => new(new HttpClient(new RawBodyHandler(body)), new TcgDexOptions());

    /// <summary>
    /// A minimal card that deserializes cleanly. Each hostile case below is this
    /// with exactly one field made wrong, so a failure is attributable to that
    /// field and not to something incidental.
    /// </summary>
    private const string ValidBase =
        """{"id":"x","name":"X","category":"Pokemon","localId":"1","set":{"id":"s","name":"S"}}""";

    /// <summary>
    /// Well-formed JSON, wrong shape. Each must surface as
    /// <see cref="TcgDexApiException"/> or read gracefully — never leak another type.
    /// </summary>
    private static IEnumerable<TestCaseData> HostileBodies()
    {
        yield return Case("hp is a string, not a number",
            """{"id":"x","name":"X","category":"Pokemon","localId":"1","set":{"id":"s","name":"S"},"hp":"lots"}""");
        yield return Case("types is a string, not an array",
            """{"id":"x","name":"X","category":"Pokemon","localId":"1","set":{"id":"s","name":"S"},"types":"Colorless"}""");
        yield return Case("attacks is a string, not an array",
            """{"id":"x","name":"X","category":"Pokemon","localId":"1","set":{"id":"s","name":"S"},"attacks":"boom"}""");
        yield return Case("boosters is a string, not an array of objects",
            """{"id":"x","name":"X","category":"Pokemon","localId":"1","set":{"id":"s","name":"S"},"boosters":"one"}""");
        yield return Case("name is null but required",
            """{"id":"x","name":null,"category":"Pokemon","localId":"1","set":{"id":"s","name":"S"}}""");
        yield return Case("set is null but required",
            """{"id":"x","name":"X","category":"Pokemon","localId":"1","set":null}""");
        yield return Case("set is present but its required id is null",
            """{"id":"x","name":"X","category":"Pokemon","localId":"1","set":{"id":null,"name":"S"}}""");
        yield return Case("damage is an object, not a scalar",
            """{"id":"x","name":"X","category":"Pokemon","localId":"1","set":{"id":"s","name":"S"},"attacks":[{"name":"A","damage":{"x":1}}]}""");
        yield return Case("localId is an array",
            """{"id":"x","name":"X","category":"Pokemon","localId":[1,2],"set":{"id":"s","name":"S"}}""");
        yield return Case("the whole body is a JSON array, not an object", "[1,2,3]");
        yield return Case("empty object, every required field missing", "{}");
        yield return Case("a non-JSON error page from a proxy", "<html><body>502 Bad Gateway</body></html>");
        yield return Case("hp overflows Int32",
            """{"id":"x","name":"X","category":"Pokemon","localId":"1","set":{"id":"s","name":"S"},"hp":99999999999999999999}""");

        // The suspected leak: a number too large for decimal reaches
        // FlexibleStringConverter.ReadNumber -> GetDecimal(), which throws
        // FormatException rather than JsonException. 1e400 may be rejected as a
        // token upstream, so the decisive case is a plain integer that is valid
        // JSON, larger than Int64 (so TryGetInt64 fails), and larger than the
        // decimal max of ~7.9e28 (so GetDecimal is the one that must throw).
        yield return Case("damage overflows decimal via a float exponent",
            """{"id":"x","name":"X","category":"Pokemon","localId":"1","set":{"id":"s","name":"S"},"attacks":[{"name":"A","damage":1e400}]}""");
        yield return Case("damage is a 31-digit integer past Int64 and decimal",
            """{"id":"x","name":"X","category":"Pokemon","localId":"1","set":{"id":"s","name":"S"},"attacks":[{"name":"A","damage":1000000000000000000000000000000}]}""");
    }

    private static TestCaseData Case(string name, string body)
        => new TestCaseData(body).SetName($"Hostile: {name}");

    [TestCaseSource(nameof(HostileBodies))]
    public async Task HostileContent_SurfacesAsTcgDexApiExceptionOrIsRead(string body)
    {
        using TcgDexClient client = Client(body);

        try
        {
            // A card that maps despite the hostility is an acceptable outcome —
            // the SDK is deliberately lenient. The contract is only about what
            // happens when it CANNOT map: one exception type, not a leak.
            _ = await client.Cards.GetAsync("x", CancellationToken.None).ConfigureAwait(false);
        }
        catch (TcgDexApiException)
        {
            // The documented outcome for a body that cannot be read.
        }
        catch (System.Exception ex)
        {
            Assert.Fail($"Leaked {ex.GetType().Name} past the one-exception contract: {ex.Message}");
        }
    }
}
