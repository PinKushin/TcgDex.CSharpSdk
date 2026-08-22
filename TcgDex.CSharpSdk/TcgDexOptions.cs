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
    /// How long one request may take, headers and body together. Defaults to
    /// 30 seconds. Use <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>
    /// to remove the limit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this the ceiling is <see cref="HttpClient"/>'s own default of
    /// <b>100 seconds</b> — a value nobody chose, which leaves a caller blocked
    /// for over a minute and a half on an endpoint that has stopped answering.
    /// The live API returns its largest response, the 2.3 MB unpaginated card
    /// list, in well under a second, so 30 seconds is around forty times the
    /// observed worst case and still well clear of a slow mobile connection.
    /// </para>
    /// <para>
    /// <b>Applied through a linked
    /// <see cref="System.Threading.CancellationTokenSource"/> rather than
    /// <see cref="HttpClient.Timeout"/>.</b> Callers may supply their own
    /// <see cref="HttpClient"/> and share it with the rest of their
    /// application, so setting a property on it would reach outside this SDK —
    /// and <see cref="HttpClient"/> throws if a request has already been sent
    /// on it. The linked source also spans the body read, which
    /// <see cref="HttpClient.Timeout"/> would cover but a timeout scoped to
    /// sending alone would not: the transport reads headers first and streams
    /// the body afterwards.
    /// </para>
    /// <para>
    /// An expiry becomes <see cref="TcgDexApiException"/>, in keeping with the
    /// single error contract. Cancellation the *caller* requested stays an
    /// <see cref="OperationCanceledException"/>, because that is theirs to
    /// observe rather than a fault to report.
    /// </para>
    /// </remarks>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether <see cref="Models.Card.Pricing"/> is populated. Defaults to
    /// <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>pricing</c> block is the most expensive part of a card to
    /// deserialize — measured at <b>4.7 µs and 2.2 KB of a 23 µs card</b>,
    /// roughly a fifth of both — and it is paid whether or not anything reads
    /// it. The API has no way to ask for a card without it: every field-
    /// selection form tried against the live service returned the identical
    /// 2,940 bytes, so this cannot be saved on the wire, only in the parse.
    /// </para>
    /// <para>
    /// Set to <see langword="false"/> in an application that never reads
    /// prices. The property is dropped from the deserialization contract, so
    /// System.Text.Json skips the block as an unknown field rather than building
    /// it and discarding it.
    /// </para>
    /// <para>
    /// <b>It defaults to on, and the reason is not performance.</b> With it off,
    /// <c>card.Pricing</c> is <see langword="null"/> for every card — which is
    /// indistinguishable from a card the API genuinely has no prices for. That
    /// turns a configuration choice into a silently wrong answer, so it is opt
    /// out rather than opt in. Against a network round trip of 20–50 ms the
    /// 4.7 µs is around 0.02% of a request; turn it off because the data is
    /// unwanted, not because it is slow.
    /// </para>
    /// </remarks>
    public bool DeserializePricing { get; set; } = true;

    /// <summary>
    /// How many deserialized responses to retain so a repeat fetch can skip the
    /// parse. Defaults to 64. Set to zero to disable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deserialization is roughly 86% of the in-process cost of a request, and
    /// the response cache does not avoid it — that cache stores bytes, because
    /// it sits on the <see cref="System.Net.Http.HttpMessageHandler"/> pipeline
    /// where <c>ETag</c> revalidation is possible and one implementation covers
    /// every endpoint. A cache hit therefore re-parsed the same bytes into the
    /// same object every time. This layer stops that.
    /// </para>
    /// <para>
    /// <b>Entries are validated by <c>ETag</c>, not by a lifetime of their
    /// own.</b> A stored model is reused only when the response carries the
    /// exact <c>ETag</c> it was built from — whether that header came from the
    /// server or from the byte cache replaying it. So a typed entry cannot be
    /// staler than the bytes underneath it, and there is no second expiry policy
    /// to keep in step with the first. A response without an <c>ETag</c> is
    /// never served from here.
    /// </para>
    /// <para>
    /// <b>Callers share one instance.</b> Two fetches of an unchanged resource
    /// now return the same object rather than two equal ones. The models are
    /// records with <c>init</c>-only properties, so this is safe for anything
    /// the type system allows; a caller who casts an
    /// <see cref="IReadOnlyList{T}"/> property back to <see cref="List{T}"/> and
    /// mutates it would corrupt the entry for everyone. Set this to zero if that
    /// is a risk your codebase cannot rule out.
    /// </para>
    /// <para>
    /// The bound is a count, and deserialized objects are several times the size
    /// of the bytes they came from — the unpaginated card list is 2.3 MB on the
    /// wire and roughly 8 MB once parsed. 64 is deliberately far below the
    /// response cache's 512 for that reason.
    /// </para>
    /// </remarks>
    public int MaxDeserializedCacheEntries { get; set; } = 64;

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
        // S3928: the names passed to ArgumentException below are this options
        // object's own properties, not parameters of Validate() (parameterless
        // by design — it validates instance state). Reporting the offending
        // property is the actionable diagnostic for the caller, and the
        // ArgumentException contract is part of the pinned public API.
#pragma warning disable S3928
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

        // InfiniteTimeSpan is -1 milliseconds, so it has to be admitted before
        // the non-positive check rather than falling foul of it. Matching
        // HttpClient's own convention rather than inventing a second one, such
        // as treating zero as "no limit" the way MaxResponseBytes does — zero
        // there is unambiguous, whereas a zero timeout reads as "give up
        // immediately" and would silently break every request.
        if (Timeout != System.Threading.Timeout.InfiniteTimeSpan && Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"Timeout must be positive, but was {Timeout}. Use " +
                "Timeout.InfiniteTimeSpan to remove the limit.",
                nameof(Timeout));
        }

        if (MaxDeserializedCacheEntries < 0)
        {
            throw new ArgumentException(
                $"MaxDeserializedCacheEntries cannot be negative, but was " +
                $"{MaxDeserializedCacheEntries}. Use zero to disable the cache.",
                nameof(MaxDeserializedCacheEntries));
        }

        if (!TcgDexLanguages.IsSupported(Language))
        {
            throw new ArgumentException(
                $"Language '{Language}' is not supported by the TCGdex API. " +
                $"Supported languages are: {string.Join(", ", TcgDexLanguages.All)}.",
                nameof(Language));
        }
#pragma warning restore S3928
    }
}
