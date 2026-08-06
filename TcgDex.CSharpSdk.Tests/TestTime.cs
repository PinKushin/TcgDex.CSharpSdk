namespace TcgDex.Tests;

/// <summary>
/// Fixed instants for tests that need a clock they control.
/// </summary>
/// <remarks>
/// <c>DateTimeOffset.UnixEpoch</c> is .NET Core 2.1+ and does not exist in
/// .NET Framework, which these tests also run on so that the netstandard2.0
/// assembly is executed rather than merely compiled. Declaring the value once
/// keeps the tests identical across every target.
/// </remarks>
internal static class TestTime
{
    /// <summary>1970-01-01T00:00:00Z.</summary>
    internal static readonly DateTimeOffset UnixEpoch =
        new(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);
}
