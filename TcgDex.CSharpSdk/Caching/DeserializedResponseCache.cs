namespace TcgDex.Caching;

/// <summary>
/// Retains deserialized responses so an unchanged resource is parsed once.
/// </summary>
/// <remarks>
/// <para>
/// This sits above <see cref="ITcgDexResponseCache"/> rather than replacing it.
/// That cache stores bytes because it lives on the
/// <see cref="System.Net.Http.HttpMessageHandler"/> pipeline, which is what
/// makes <c>ETag</c> revalidation possible and lets one implementation serve
/// every endpoint. The cost of that placement is that a cache hit still had to
/// parse the same bytes into the same object on every call, and parsing is
/// roughly 86% of the in-process work.
/// </para>
/// <para>
/// <b>Entries are keyed to an <c>ETag</c>, which is the entire safety
/// argument.</b> A stored model is handed back only when the current response
/// carries the exact tag the model was built from — whether the server sent that
/// header or the byte cache replayed it. A typed entry therefore cannot be
/// staler than the bytes underneath it. There is deliberately no lifetime here:
/// a second expiry policy would be a second thing to keep in step with the
/// first, and the first is the one that already knows.
/// </para>
/// <para>
/// The key carries the type as well as the URL. Nothing in the public surface
/// reaches a URL served as two different models today, but a key that ignored
/// the type would hand a <c>Card</c> to a caller asking for a <c>Serie</c>, and
/// that surfaces as an <see cref="InvalidCastException"/> a long way from the
/// cause.
/// </para>
/// </remarks>
internal sealed class DeserializedResponseCache
{
    /// <summary>
    /// A struct value, so it lives inside the store's entry rather than in a
    /// second heap object.
    /// </summary>
    private readonly BoundedLru<Key, (object Value, string ETag)> _entries;

    /// <summary>Creates a cache holding at most <paramref name="maxEntries"/> models.</summary>
    /// <param name="maxEntries">The upper bound on retained models.</param>
    internal DeserializedResponseCache(int maxEntries)
        => _entries = new BoundedLru<Key, (object, string)>(maxEntries);

    /// <summary>
    /// Returns the stored model when it was built from this exact <c>ETag</c>.
    /// </summary>
    /// <typeparam name="T">The model type.</typeparam>
    /// <param name="uri">The request URI.</param>
    /// <param name="etag">The <c>ETag</c> on the current response.</param>
    /// <param name="value">The retained model, when it is still valid.</param>
    /// <returns><see langword="true"/> when the parse can be skipped.</returns>
    internal bool TryGet<T>(Uri uri, string? etag, out T value)
        where T : class
    {
        value = null!;

        // No tag means no way to know the body is the one this was built from,
        // so there is nothing to validate against and the cache stays out of it.
        // A null check rather than a length check: this comes from
        // HttpResponseHeaders.ETag, which is null or a parsed tag and never an
        // empty string, so testing for one would be testing an unreachable case.
        if (etag is null)
        {
            return false;
        }

        if (!_entries.TryGet(new Key(uri, typeof(T)), out var entry)
            || !string.Equals(entry.ETag, etag, StringComparison.Ordinal))
        {
            return false;
        }

        // The type is part of the key, so this cast cannot fail — a different T
        // is a different entry.
        value = (T)entry.Value;

        return true;
    }

    /// <summary>Retains a freshly parsed model against the tag it came with.</summary>
    /// <typeparam name="T">The model type.</typeparam>
    /// <param name="uri">The request URI.</param>
    /// <param name="etag">The <c>ETag</c> on the response it was parsed from.</param>
    /// <param name="value">The model.</param>
    internal void Set<T>(Uri uri, string? etag, T value)
        where T : class
    {
        // Efficiency rather than correctness, and mutation testing is right that
        // deleting it changes no result: TryGet rejects an empty tag before it
        // ever looks, so a tagless entry could never be served. What it would do
        // is occupy a slot and push out an entry that can be, which only shows
        // up as a lower hit rate — invisible to an assertion about a value.
        if (etag is null)
        {
            return;
        }

        _entries.Set(new Key(uri, typeof(T)), (value, etag));
    }

    /// <summary>A URL and the type it was deserialized as.</summary>
    private readonly struct Key(Uri uri, Type type) : IEquatable<Key>
    {
        private readonly Uri _uri = uri;
        private readonly Type _type = type;

        // Both halves are compared, and mutation testing reports swapping the
        // && for a || as surviving. It does, but only because GetHashCode mixes
        // both halves too, so two keys differing in either one land in different
        // buckets and Equals is never consulted. That is a masked mutant rather
        // than a harmless one: it would become live the moment two keys collided
        // in the hash, which is exactly when this method is load-bearing.
        public bool Equals(Key other)
            => _type == other._type && _uri == other._uri;

        public override bool Equals(object? obj) => obj is Key other && Equals(other);

        // Uri.GetHashCode is ordinal-ignore-case over the whole URL, which is
        // right here: the transport builds these itself, so two that differ only
        // by case are the same request.
        public override int GetHashCode()
            => unchecked((_uri.GetHashCode() * 397) ^ _type.GetHashCode());
    }

}
