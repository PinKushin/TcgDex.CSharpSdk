namespace TcgDex;

using System.Net.Http.Headers;
using System.Reflection;

/// <summary>
/// The <c>User-Agent</c> this SDK identifies itself with on every request.
/// </summary>
/// <remarks>
/// <para>
/// Requested directly by TCGdex (Avior), while debugging an unrelated latency report: *"Might
/// be better to set a standard one with the version of the SDK them[selves]"* — this SDK sent
/// none at all before, because <see cref="HttpClient"/> adds no default the way a browser or
/// <c>curl</c> does. Traffic from every consumer of this library was indistinguishable from any
/// other .NET client hitting the API.
/// </para>
/// <para>
/// This is not a cosmetic courtesy. Avior, on why it matters: *"i store logs for stats and
/// debugging purposes and to respect GDPR i don't store ip addresses, so the only way i can
/// 'identify' [a client] is by using the user agent"* — with no default header at all, every
/// request this SDK ever sent was, by TCGdex's own design, unidentifiable.
/// </para>
/// <para>
/// Applied per <see cref="HttpRequestMessage"/> rather than via
/// <see cref="HttpClient.DefaultRequestHeaders"/>, so it works identically whether the SDK owns
/// the <see cref="HttpClient"/> (<see cref="TcgDexClient.Create"/>, <c>AddTcgDex</c>) or a caller
/// supplied their own and shares it with the rest of their application — setting a property on
/// someone else's shared client would reach outside this SDK, the same reasoning
/// <see cref="RequestBudget"/> already applies to the request timeout.
/// </para>
/// <para>
/// The version comes from <see cref="AssemblyName.Version"/>, not
/// <see cref="AssemblyInformationalVersionAttribute"/>. Both would read "0.6.0" today — this
/// repository sets no build-metadata suffix — but <see cref="AssemblyName.Version"/> is
/// fundamental assembly identity the runtime always preserves, while a custom attribute is
/// metadata a trimmer can remove when nothing else references it. Native AOT is verified here by
/// actually publishing and running a binary
/// (<c>TcgDex.CSharpSdk.AotSmokeTest</c>), so this reads the value guaranteed not to need that
/// verification to prove safe.
/// </para>
/// </remarks>
internal static class TcgDexUserAgent
{
    /// <summary>
    /// Computed once. A per-request allocation here would be wasted work — every request from a
    /// given process sends the identical value.
    /// </summary>
    private static readonly ProductInfoHeaderValue s_product = BuildProduct();

    /// <summary>
    /// Sets the <c>User-Agent</c> header on <paramref name="request"/>.
    /// </summary>
    /// <param name="request">The request to identify.</param>
    internal static void Apply(HttpRequestMessage request)
        => request.Headers.UserAgent.Add(s_product);

    private static ProductInfoHeaderValue BuildProduct()
    {
        Version? version = typeof(TcgDexUserAgent).Assembly.GetName().Version;

        // Major.Minor.Build only, dropping the fourth (Revision) component MSBuild always sets to
        // 0 — "0.6.0.0" reporting itself as version "0.6.0.0" when the package is "0.6.0" would be
        // a needless mismatch for whoever is reading the header.
        //
        // The null branch is not unit-tested and left that way deliberately: Assembly.GetName()
        // .Version is null only for a dynamically-emitted assembly with no version ever set, which
        // this compiled library can never be. Making BuildProduct internal purely to reflection-
        // invoke it with a fabricated null would trade a genuinely private implementation detail
        // for coverage of a branch with no realistic path to firing.
        string productVersion = version is null
            ? "unknown"
            : $"{version.Major}.{version.Minor}.{version.Build}";

        return new ProductInfoHeaderValue("TcgDex.CSharpSdk", productVersion);
    }
}
