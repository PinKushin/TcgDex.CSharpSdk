namespace TcgDex;

/// <summary>
/// What went wrong, for a <see cref="TcgDexApiException"/>.
/// </summary>
/// <remarks>
/// Lets a caller branch on the failure without parsing the message — retrying a
/// <see cref="Timeout"/> or <see cref="Network"/> failure, say, but not an
/// <see cref="InvalidResponse"/>, which will fail the same way again.
/// </remarks>
public enum TcgDexErrorKind
{
    /// <summary>
    /// Not classified. Only an exception constructed without a kind, such as
    /// one a caller builds in a test, carries this.
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// The API answered with an error status. <see cref="TcgDexApiException.StatusCode"/>
    /// holds it, and <see cref="TcgDexApiException.IsLanguageError"/> tells an
    /// unsupported language apart from other <c>404</c>s.
    /// </summary>
    HttpStatus,

    /// <summary>
    /// The request could not be sent, or the connection failed while the body
    /// was being read.
    /// </summary>
    Network,

    /// <summary>
    /// The request took longer than <see cref="TcgDexOptions.Timeout"/>.
    /// </summary>
    Timeout,

    /// <summary>
    /// Something in the HTTP pipeline other than the caller cancelled the
    /// request, such as <see cref="System.Net.Http.HttpClient.Timeout"/>. The
    /// caller's own cancellation is an <see cref="OperationCanceledException"/>
    /// instead, never this.
    /// </summary>
    Canceled,

    /// <summary>
    /// The body exceeded <see cref="TcgDexOptions.MaxResponseBytes"/>.
    /// </summary>
    ResponseTooLarge,

    /// <summary>
    /// The response arrived but could not be used: malformed JSON, or no
    /// content where the endpoint always has some.
    /// </summary>
    InvalidResponse,

    /// <summary>
    /// The GraphQL endpoint answered with an <c>errors</c> array.
    /// </summary>
    GraphQLError,
}
