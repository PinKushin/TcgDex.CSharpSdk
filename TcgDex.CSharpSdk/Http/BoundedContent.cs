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
/// <para>
/// <b>Bytes, not a string.</b> The first version of this returned a decoded
/// <see cref="string"/>, which cost two full copies of every body — one for
/// <c>ToArray</c> and one for <c>Encoding.UTF8.GetString</c> — before parsing
/// even started. System.Text.Json reads UTF-8 directly, so handing it the
/// buffer that was already filled removes both. Benchmarking against another
/// SDK is what surfaced it: the size limit was costing roughly three times the
/// allocations of the payload it was protecting.
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
    /// Reads <paramref name="content"/> as UTF-8 bytes, failing if it exceeds
    /// <paramref name="maxBytes"/>.
    /// </summary>
    /// <param name="content">The response body.</param>
    /// <param name="maxBytes">The ceiling in bytes; zero or less means no limit.</param>
    /// <param name="uri">The request URI, used only in the failure message.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>
    /// The body, as a segment over a buffer this method owns. Valid until the
    /// caller finishes with it; nothing else references it.
    /// </returns>
    /// <exception cref="TcgDexApiException">The body exceeded the ceiling.</exception>
    internal static async Task<ArraySegment<byte>> ReadAsBytesAsync(
        HttpContent content,
        long maxBytes,
        Uri uri,
        CancellationToken cancellationToken)
    {
        Guard.NotNull(content);

        if (maxBytes <= 0)
        {
#if NETSTANDARD2_0
            cancellationToken.ThrowIfCancellationRequested();
            var unbounded = await content.ReadAsByteArrayAsync().ConfigureAwait(false);
#else
            var unbounded = await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
#endif
            return new ArraySegment<byte>(unbounded);
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

        // Pre-sized from Content-Length when the sender declares one, which is
        // the ordinary case. An expandable MemoryStream doubles as it fills, so
        // a 10 KB body churns through 256, 512, 1K ... 16K buffers and allocates
        // several times the payload before anything is parsed. Starting at the
        // right size makes that a single allocation.
        //
        // The declared length is still not trusted: it has already been checked
        // against the limit above, and the byte counting below remains the only
        // thing that enforces it. This uses the value as a *hint* for capacity,
        // where being wrong costs a resize rather than a bypassed guard.
        var capacity = content.Headers.ContentLength is { } hint && hint > 0 && hint <= maxBytes
            ? (int)Math.Min(hint, int.MaxValue)
            : 0;

        // Rented, not allocated. A fresh 16 KB chunk on every request was by
        // far the largest single cost in this method — larger than the payload
        // it was reading — and it is pure scratch space that never outlives the
        // call. Renting is what a per-request buffer should always have been.
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(ChunkSize);

        try
        {
            using var buffered = capacity > 0 ? new MemoryStream(capacity) : new MemoryStream();

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

                // Checked before writing, so an oversized body is abandoned at
                // the limit instead of being fully buffered and then rejected.
                // Reading it all first would concede the memory this exists to
                // protect.
                if (buffered.Length + read > maxBytes)
                {
                    throw TooLarge(uri, maxBytes, null);
                }

                buffered.Write(buffer, 0, read);
            }

            // TryGetBuffer rather than ToArray: the segment views the stream's
            // own array, so the bytes are not copied a second time. A
            // MemoryStream built with the parameterless constructor always
            // exposes its buffer, but the fallback keeps this honest if that
            // ever changes.
            return buffered.TryGetBuffer(out var segment)
                ? segment
                : new ArraySegment<byte>(buffered.ToArray());
        }
        finally
        {
            // No clearing: this holds a public API's response body, not a
            // secret, and clearing 16 KB per request to hide it from the next
            // renter would cost more than it protects.
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
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
