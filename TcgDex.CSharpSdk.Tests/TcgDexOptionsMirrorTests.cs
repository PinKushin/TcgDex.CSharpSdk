namespace TcgDex.Tests;

using System;

[TestFixture]
public sealed class TcgDexOptionsMirrorTests
{
    [TestCase(TcgDexMirror.Eu1, "https://api.eu1.tcgdex.net/v2/", "https://api.eu1.tcgdex.net/v2/graphql")]
    [TestCase(TcgDexMirror.Eu2, "https://api.eu2.tcgdex.net/v2/", "https://api.eu2.tcgdex.net/v2/graphql")]
    [TestCase(TcgDexMirror.Eu3, "https://api.eu3.tcgdex.net/v2/", "https://api.eu3.tcgdex.net/v2/graphql")]
    [TestCase(TcgDexMirror.Na1, "https://api.na1.tcgdex.net/v2/", "https://api.na1.tcgdex.net/v2/graphql")]
    [TestCase(TcgDexMirror.Na2, "https://api.na2.tcgdex.net/v2/", "https://api.na2.tcgdex.net/v2/graphql")]
    [TestCase(TcgDexMirror.As1, "https://api.as1.tcgdex.net/v2/", "https://api.as1.tcgdex.net/v2/graphql")]
    public void UseMirror_PointsBothEndpointsAtTheNode(TcgDexMirror mirror, string baseAddress, string graphql)
    {
        TcgDexOptions options = new();

        TcgDexOptions returned = options.UseMirror(mirror);

        options.BaseAddress.ShouldBe(new Uri(baseAddress));
        options.GraphQlEndpoint.ShouldBe(new Uri(graphql));
        returned.ShouldBeSameAs(options);
    }

    [Test]
    public void UseMirror_AnUndefinedMirror_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() => new TcgDexOptions().UseMirror((TcgDexMirror)999));

    [Test]
    public void UseMirror_ProducesOptionsThatStillValidate() =>
        Should.NotThrow(() => new TcgDexOptions().UseMirror(TcgDexMirror.Na2).Validate());
}
