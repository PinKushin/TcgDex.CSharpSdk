namespace TcgDex;

using System.IO;

/// <summary>
/// Reads a response body with a ceiling on how much will be buffered.
/// </summary>
/// <remarks>
/// <para>
/// A body is materialised in memory before it is deserialized, so without a
/// ceiling the peak memory of a request is whatever the server chooses to send.
/// Automatic decompression makes that sharper rather than softer: a few
/// kilobytes of hostile gzip expand to gigabytes, and the expansion happens in
/// the handler *below* this code, so counting here counts decompressed bytes —
/// which is where the limit does any good.
/// </para>
/// <para>
/// This lives in the transport rather than on
/// <see cref="System.Net.Http.HttpClient.MaxResponseContentBufferSize"/> because
/// callers may supply their own <see cref="System.Net.Http.HttpClient"/>, and
/// that constructor-injected case is exactly the one an SDK cannot configure.
/// </para>
/// </remarks>
internal static class BoundedContent
{
    /// <summary>
    /// Buffer size for the copy loop. Large enough that ordinary responses take
    /// few iterations, small enough that it is not itself worth attacking.
    /// </summary>
    private const int ChunkSize = 16 * 1024;

    /// <summary>
    /// Reads <paramref name="content"/> as a string, failing if it exceeds
    /// <paramref name="maxBytes"/>.
    /// </summary>
    /// <param name="content">The response body.</param>
    /// <param name="maxBytes">The ceiling in bytes; zero or less means no limit.</param>
    /// <param name="uri">The request URI, used only in the failure message.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The decoded body.</returns>
    /// <exception cref="TcgDexApiException">The body exceeded the ceiling.</exception>
    internal static async Task<string> ReadAsStringAsync(
        HttpContent content,
        long maxBytes,
        Uri uri,
        CancellationToken cancellationToken)
    {
        Guard.NotNull(content);

        if (maxBytes <= 0)
        {
            return await content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        // Content-Length is a claim by the sender, so it is worth acting on
        // when it already admits the body is too big — it saves transferring
        // it — but it is never treated as proof that the body is small enough.
        // The byte count below is what actually enforces the limit.
        if (content.Headers.ContentLength is { } declared && declared > maxBytes)
        {
            throw TooLarge(uri, maxBytes, declared);
        }

#if NETSTANDARD2_0
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = await content.ReadAsStreamAsync().ConfigureAwait(false);
#else
        using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
#endif

        var buffer = new byte[ChunkSize];
        using var buffered = new MemoryStream();

        while (true)
        {
#if NETSTANDARD2_0
            // Stream.ReadAsync(Memory<byte>, ...) is .NET Core 2.1+. CA1835
            // prefers it and is right where it exists, so the targets split
            // rather than the whole SDK dropping to the array overload.
            var read = await stream
                .ReadAsync(buffer, 0, buffer.Length, cancellationToken)
                .ConfigureAwait(false);
#else
            var read = await stream
                .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                .ConfigureAwait(false);
#endif

            if (read == 0)
            {
                break;
            }

            // Checked before writing, so an oversized body is abandoned at the
            // limit instead of being fully buffered and then rejected. Reading
            // it all first would concede the memory this exists to protect.
            if (buffered.Length + read > maxBytes)
            {
                throw TooLarge(uri, maxBytes, null);
            }

            buffered.Write(buffer, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(buffered.ToArray(), 0, (int)buffered.Length);
    }

    private static TcgDexApiException TooLarge(Uri uri, long maxBytes, long? declared)
    {
        var size = declared is { } length
            ? $"declared {length} bytes"
            : $"exceeded {maxBytes} bytes";

        return new TcgDexApiException(
            $"The response from '{uri}' {size}, over the {maxBytes} byte limit set by " +
            $"{nameof(TcgDexOptions)}.{nameof(TcgDexOptions.MaxResponseBytes)}. " +
            "Raise that limit if the endpoint legitimately returns responses this large.");
    }
}
