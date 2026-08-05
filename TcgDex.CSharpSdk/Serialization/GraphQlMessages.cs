namespace TcgDex.Serialization;

using TcgDex.Models;

/// <summary>
/// Serialization metadata for the GraphQL wire types.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="TcgDexJsonContext"/> and marked internal
/// because the generator emits a public property per registered type. Putting
/// these envelopes in the public context would force the wire format itself to
/// become public API.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GraphQlRequest))]
[JsonSerializable(typeof(GraphQlCardsResponse))]
internal sealed partial class GraphQlJsonContext : JsonSerializerContext
{
}

/// <summary>The request body sent to the GraphQL endpoint.</summary>
/// <param name="Query">The GraphQL document.</param>
internal sealed record GraphQlRequest(string Query);

/// <summary>A single error returned alongside or instead of data.</summary>
internal sealed record GraphQlError
{
    /// <summary>Human-readable description of what went wrong.</summary>
    public string? Message { get; init; }
}

/// <summary>The <c>data</c> payload for a card search.</summary>
internal sealed record GraphQlCardsData
{
    /// <summary>
    /// The matching cards. Entries can be null when the server fails to resolve
    /// a non-nullable field on one card.
    /// </summary>
    public IReadOnlyList<Card?>? Cards { get; init; }
}

/// <summary>The full response envelope for a card search.</summary>
internal sealed record GraphQlCardsResponse
{
    /// <summary>The returned data, absent when the query failed outright.</summary>
    public GraphQlCardsData? Data { get; init; }

    /// <summary>
    /// Errors reported by the server. GraphQL returns HTTP 200 even for a failed
    /// query, so this is the only reliable failure signal.
    /// </summary>
    public IReadOnlyList<GraphQlError>? Errors { get; init; }
}
