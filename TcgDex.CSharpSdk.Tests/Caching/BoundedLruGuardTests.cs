namespace TcgDex.Tests.Caching;

using TcgDex.Caching;

/// <summary>
/// The bound is validated by the store itself.
/// </summary>
/// <remarks>
/// <see cref="MemoryTcgDexResponseCache"/> runs the same guard with the same parameter name
/// before it builds one, so through that cache neither guard can be told from the other.
/// Constructing the store directly is the only way to reach this one.
/// </remarks>
[TestFixture]
public sealed class BoundedLruGuardTests
{
    [TestCase(0)]
    [TestCase(-1)]
    public void ABoundBelowOne_Throws_NamingMaxEntries(int maxEntries)
        => Should.Throw<ArgumentOutOfRangeException>(() => new BoundedLru<string, int>(maxEntries))
            .ParamName.ShouldBe("maxEntries");
}
