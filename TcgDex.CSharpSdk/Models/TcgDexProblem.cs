namespace TcgDex.Models;

/// <summary>
/// The error body the API returns for a failed request.
/// </summary>
/// <remarks>
/// <para>
/// The live API returns an RFC 9457-shaped problem document — the current
/// specification, which obsoletes RFC 7807; the wire format is unchanged.
/// Note that a
/// <c>404</c> does not necessarily mean the resource is missing — an
/// unsupported language also returns <c>404</c>, with
/// <see cref="Type"/> ending in <c>language-invalid</c>. Discriminate on
/// <see cref="Type"/>, not on the status code alone.
/// </para>
/// <para>
/// The published documentation still describes a simpler <c>{"error": "..."}</c>
/// body. That form was not observed from the live API, but
/// <see cref="Error"/> captures it should it reappear.
/// </para>
/// </remarks>
public sealed record TcgDexProblem
{
    /// <summary>
    /// A URI identifying the error kind, for example
    /// <c>https://tcgdex.dev/errors/not-found</c> or
    /// <c>https://tcgdex.dev/errors/language-invalid</c>.
    /// </summary>
    public string? Type { get; init; }

    /// <summary>A human-readable summary of the problem.</summary>
    public string? Title { get; init; }

    /// <summary>The HTTP status code, repeated in the body.</summary>
    public int? Status { get; init; }

    /// <summary>The request path that failed.</summary>
    public string? Endpoint { get; init; }

    /// <summary>The HTTP method used.</summary>
    public string? Method { get; init; }

    /// <summary>The offending language code, on a language error.</summary>
    public string? Lang { get; init; }

    /// <summary>Additional explanation, such as the list of valid languages.</summary>
    public string? Details { get; init; }

    /// <summary>
    /// The message from the legacy error shape, if the API ever returns it.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Whether this problem reports an unsupported language rather than a
    /// missing resource. Both are returned as <c>404</c>.
    /// </summary>
    public bool IsLanguageError
        => Type is not null && Type.EndsWith("language-invalid", StringComparison.Ordinal);

    /// <summary>
    /// The best available description of the problem, preferring
    /// <see cref="Details"/>, then <see cref="Title"/>, then <see cref="Error"/>.
    /// </summary>
    /// <returns>A description, or a generic fallback when the body was empty.</returns>
    public string Describe()
        => Details ?? Title ?? Error ?? "The TCGdex API returned an error with no description.";
}
