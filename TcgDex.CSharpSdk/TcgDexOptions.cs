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

        if (!TcgDexLanguages.IsSupported(Language))
        {
            throw new ArgumentException(
                $"Language '{Language}' is not supported by the TCGdex API. " +
                $"Supported languages are: {string.Join(", ", TcgDexLanguages.All)}.",
                nameof(Language));
        }
    }
}
