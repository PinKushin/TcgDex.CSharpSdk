namespace TcgDex.Tests;

using TcgDex;

/// <summary><see cref="TcgDexLanguages.All"/> is shared process-wide, so it must not be mutable.</summary>
[TestFixture]
public sealed class TcgDexLanguagesTests
{
    [Test]
    public void All_CannotBeMutatedThroughACast()
    {
        IList<string> list = TcgDexLanguages.All.ShouldBeAssignableTo<IList<string>>()!;

        Should.Throw<NotSupportedException>(() => list[0] = "xx");
        TcgDexLanguages.All[0].ShouldBe(TcgDexLanguages.English);
    }

    [Test]
    public void All_IsNotAnArray()
        => TcgDexLanguages.All.ShouldNotBeOfType<string[]>("an array behind the interface can be cast back and written");
}
