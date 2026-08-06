namespace TcgDex;

/// <summary>
/// Argument checks written portably.
/// </summary>
/// <remarks>
/// <para>
/// The BCL grew <c>ArgumentNullException.ThrowIfNull</c> and friends in .NET 6
/// and 7. They do not exist on <c>netstandard2.0</c>, and static members cannot
/// be added to a type from outside it, so the call sites go through here
/// instead of being wrapped in <c>#if</c> at every use.
/// </para>
/// <para>
/// The same code runs on every target — no conditional compilation — so a guard
/// cannot behave differently depending on which assembly a consumer resolved.
/// The exception types and parameter names match what the BCL helpers throw,
/// because callers catch <see cref="ArgumentNullException"/> and read
/// <see cref="ArgumentException.ParamName"/>.
/// </para>
/// </remarks>
internal static class Guard
{
    /// <summary>Throws <see cref="ArgumentNullException"/> if <paramref name="value"/> is null.</summary>
    internal static void NotNull(
#if !NETSTANDARD2_0
        // Tells the caller's flow analysis that value is non-null once this
        // returns. netstandard2.0 has no accessible NotNullAttribute — one of
        // the compatibility packages ships an internal copy that occupies the
        // name — so the post-condition is simply not expressed there. It costs
        // nothing: the modern targets compile the same call sites and would
        // fail the build if any of them relied on a null slipping through.
        [System.Diagnostics.CodeAnalysis.NotNull]
#endif
        object? value,
        [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        if (value is null)
        {
            throw new ArgumentNullException(paramName);
        }
    }

    /// <summary>
    /// Throws if <paramref name="value"/> is null, empty, or only whitespace —
    /// <see cref="ArgumentNullException"/> for null, matching the BCL's split.
    /// </summary>
    internal static void NotNullOrWhiteSpace(
#if !NETSTANDARD2_0
        [System.Diagnostics.CodeAnalysis.NotNull]
#endif
        string? value,
        [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        if (value is null)
        {
            throw new ArgumentNullException(paramName);
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("The value cannot be empty or composed entirely of whitespace.", paramName);
        }
    }

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> if <paramref name="value"/> is below <paramref name="minimum"/>.</summary>
    internal static void NotLessThan(
        int value,
        int minimum,
        [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        if (value < minimum)
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                value,
                $"The value must be greater than or equal to {minimum}.");
        }
    }
}
