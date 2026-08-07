namespace TcgDex;

/// <summary>
/// Configuration for the TCGdex client.
/// </summary>
public sealed class TcgDexOptions
{
    /// <summary>
    /// The API root, without the language segment. Defaults to the official
    /// host.
    /// </summary>
    /// <remarks>
    /// Overridable so callers can target a mirror or a local test server. The
    /// trailing slash matters — it is what makes the language and resource
    /// segments append rather than replace the path.
    /// </remarks>
    public Uri BaseAddress { get; set; } = new("https://api.tcgdex.net/v2/");

    /// <summary>
    /// The language segment used for every request. Defaults to English.
    /// See <see cref="TcgDexLanguages"/> for the accepted values.
    /// </summary>
    public string Language { get; set; } = TcgDexLanguages.English;

    /// <summary>
    /// The GraphQL endpoint, used only by the opt-in projection and nested-fetch
    /// paths.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <see cref="BaseAddress"/>: GraphQL lives
    /// outside the language segment because it has no language support at all.
    /// </remarks>
    public Uri GraphQlEndpoint { get; set; } = new("https://api.tcgdex.net/v2/graphql");

    /// <summary>
    /// The largest response body the client will buffer, in bytes. Defaults to
    /// 32 MiB. Set to zero to remove the limit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A response is read into memory before it is deserialized, so without a
    /// ceiling the peak memory of a request is whatever the server chooses to
    /// send. Compression makes that worse rather than better: a few kilobytes
    /// of hostile gzip can expand to gigabytes, and the expansion happens in
    /// the handler below this one, so the limit is applied to the *decompressed*
    /// bytes where it actually protects anything.
    /// </para>
    /// <para>
    /// The default is generous on purpose. The largest response the API
    /// produces is the unpaginated card list at roughly 2.4 MB, so 32 MiB
    /// leaves an order of magnitude of headroom while still bounding memory.
    /// Raise it if you target a mirror that serves something larger.
    /// </para>
    /// </remarks>
    public long MaxResponseBytes { get; set; } = 32L * 1024 * 1024;

    /// <summary>
    /// Throws when the options cannot produce valid requests.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The language is not one the API accepts, or the base address is not
    /// absolute.
    /// </exception>
    /// <remarks>
    /// Validating up front turns a typo'd language into an immediate, readable
    /// failure rather than a 404 on the first call that looks like a missing
    /// card.
    /// </remarks>
    public void Validate()
    {
        if (!BaseAddress.IsAbsoluteUri)
        {
            throw new ArgumentException(
                $"BaseAddress must be an absolute URI, but was '{BaseAddress}'.",
                nameof(BaseAddress));
        }

        if (MaxResponseBytes < 0)
        {
            throw new ArgumentException(
                $"MaxResponseBytes cannot be negative, but was {MaxResponseBytes}. " +
                "Use zero to remove the limit.",
                nameof(MaxResponseBytes));
        }

        if (!TcgDexLanguages.IsSupported(Language))
        {
            throw new ArgumentException(
                $"Language '{Language}' is not supported by the TCGdex API. " +
                $"Supported languages are: {string.Join(", ", TcgDexLanguages.All)}.",
                nameof(Language));
        }
    }
}
