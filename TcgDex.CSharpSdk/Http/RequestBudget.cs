namespace TcgDex;

/// <summary>
/// The ceiling on one request, as a token linked to the caller's own.
/// </summary>
/// <remarks>
/// <para>
/// Shared by both transports because they need the identical guarantee, and
/// because the GraphQL one going without it was invisible: it simply inherited
/// <see cref="HttpClient"/>'s 100-second default — the value
/// <see cref="TcgDexOptions.Timeout"/> exists to replace — and then reported the
/// expiry as "the GraphQL request timed out", naming a limit the SDK had not set
/// and could not have told you.
/// </para>
/// <para>
/// Linked rather than applied to <see cref="HttpClient.Timeout"/>: callers may
/// supply their own client and share it with the rest of their application, so
/// setting a property on it would reach outside this SDK — and
/// <see cref="HttpClient"/> throws if a request has already been sent on it.
/// </para>
/// <para>
/// The caller's own token must be kept alongside the returned one. Distinguishing
/// "the budget expired" from "the caller asked to stop" is what decides whether
/// the failure becomes a <see cref="TcgDexApiException"/> or stays an
/// <see cref="OperationCanceledException"/>, and the linked token cannot tell
/// them apart on its own.
/// </para>
/// </remarks>
internal static class RequestBudget
{
    /// <summary>
    /// A source that expires after <paramref name="timeout"/>, or
    /// <see langword="null"/> when no limit applies.
    /// </summary>
    /// <param name="timeout">
    /// The ceiling, or <see cref="Timeout.InfiniteTimeSpan"/> for none.
    /// </param>
    /// <param name="cancellationToken">The caller's token, linked into the result.</param>
    internal static CancellationTokenSource? Create(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
        {
            return null;
        }

        CancellationTokenSource source =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        source.CancelAfter(timeout);

        return source;
    }
}
