namespace TcgDex.Diagnostics;

/// <summary>
/// The logger category the SDK writes under.
/// </summary>
/// <remarks>
/// <para>
/// Every log message the SDK emits uses this one category, so a single rule
/// configures all of it. In <c>appsettings.json</c> that is the string itself
/// (<c>"Logging": { "LogLevel": { "TcgDex": "Debug" } }</c>); in code, use the
/// constant:
/// </para>
/// <code>
/// builder.Logging.AddFilter(TcgDexLogging.CategoryName, LogLevel.Debug);
/// </code>
/// </remarks>
public static class TcgDexLogging
{
    /// <summary>
    /// The logger category every SDK message is written under. Stable across versions.
    /// </summary>
    public const string CategoryName = "TcgDex";
}
