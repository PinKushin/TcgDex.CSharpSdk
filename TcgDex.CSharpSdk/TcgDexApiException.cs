namespace TcgDex;

using System.Net;
using TcgDex.Models;

/// <summary>
/// Thrown when the TCGdex API returns a failure the caller cannot reasonably
/// treat as an ordinary result.
/// </summary>
/// <remarks>
/// A missing resource is <em>not</em> represented by this exception — the
/// single-item getters return <see langword="null"/> for that, because asking
/// for a card that does not exist is a normal outcome. This exception means the
/// request itself was wrong or the service failed: an unsupported language, a
/// server error, or a response body that could not be parsed.
/// </remarks>
public sealed class TcgDexApiException : Exception
{
    /// <summary>Creates an exception with no further detail.</summary>
    public TcgDexApiException()
    {
    }

    /// <summary>Creates an exception with a message.</summary>
    /// <param name="message">The error description.</param>
    public TcgDexApiException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a message and underlying cause.</summary>
    /// <param name="message">The error description.</param>
    /// <param name="innerException">The underlying cause.</param>
    public TcgDexApiException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates an exception describing a failed API response.</summary>
    /// <param name="message">The error description.</param>
    /// <param name="statusCode">The HTTP status returned.</param>
    /// <param name="problem">The parsed problem document, when the body contained one.</param>
    /// <param name="innerException">The underlying cause, if any.</param>
    public TcgDexApiException(
        string message,
        HttpStatusCode statusCode,
        TcgDexProblem? problem = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        Problem = problem;
    }

    /// <summary>The HTTP status code the API returned, when there was a response.</summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>
    /// The parsed error body, when the API returned one that could be read.
    /// </summary>
    public TcgDexProblem? Problem { get; }

    /// <summary>
    /// Whether the failure was an unsupported language rather than a genuine
    /// server or request error.
    /// </summary>
    /// <remarks>
    /// The API reports this as <c>404</c>, the same status as a missing
    /// resource, so the status code alone cannot distinguish the two.
    /// </remarks>
    public bool IsLanguageError => Problem?.IsLanguageError ?? false;
}
