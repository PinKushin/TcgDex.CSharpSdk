// netstandard2.0 only. On every other target HttpContent already has these
// overloads, the instance methods win overload resolution, and this class would
// be code that ships and is never called — invisible to the coverage gate
// because it cannot be reached, not because it was tested.
#if NETSTANDARD2_0

namespace TcgDex;

/// <summary>
/// Cancellable reads of an <see cref="HttpContent"/> body.
/// </summary>
/// <remarks>
/// <para>
/// <c>HttpContent.ReadAsStringAsync(CancellationToken)</c> and its byte-array
/// counterpart arrived in .NET 5, so on <c>netstandard2.0</c> only the
/// token-less overloads exist. These give the SDK one call shape across
/// targets.
/// </para>
/// <para>
/// The behaviour is **not** equivalent, and the difference is worth stating
/// plainly rather than hiding: the token is observed before the read begins,
/// but a read already in flight cannot be interrupted. Cancelling mid-body
/// therefore takes effect when the body finishes arriving, not immediately.
/// .NET Framework and Unity consumers get best-effort cancellation here; every
/// modern target gets the real thing.
/// </para>
/// </remarks>
internal static class HttpContentExtensions
{
    /// <summary>Reads the body as a string, observing <paramref name="cancellationToken"/>.</summary>
    internal static Task<string> ReadAsStringAsync(
        this HttpContent content,
        CancellationToken cancellationToken)
    {
        Guard.NotNull(content);
        cancellationToken.ThrowIfCancellationRequested();

        return content.ReadAsStringAsync();
    }

    /// <summary>Reads the body as bytes, observing <paramref name="cancellationToken"/>.</summary>
    internal static Task<byte[]> ReadAsByteArrayAsync(
        this HttpContent content,
        CancellationToken cancellationToken)
    {
        Guard.NotNull(content);
        cancellationToken.ThrowIfCancellationRequested();

        return content.ReadAsByteArrayAsync();
    }
}

#endif
