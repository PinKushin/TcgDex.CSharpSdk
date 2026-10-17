namespace TcgDex.Tests.Querying;

using TcgDex.Querying;

/// <summary>
/// Value-side expressions the translator evaluates by interpretation, never by
/// <c>Expression.Compile()</c> (DECISIONS #4), and the escaping of the value
/// once it is read.
/// </summary>
[TestFixture]
public sealed class ValueExpressionTests
{
    private static string Query(System.Linq.Expressions.Expression<Func<TcgDex.Models.Card, bool>> predicate)
        => new CardQuery().Where(predicate).ToQueryString();

    // ----- escaping happens exactly once -----

    [Test]
    public void Contains_APercentEncodedLiteral_IsSentAsThatLiteral()
    {
        // `%2A` used to be unescaped to `*` first, turning a literal search into
        // a wildcard that matched every card.
        Query(c => c.Name.Contains("%2A")).ShouldBe("name=%252A");
    }

    [Test]
    public void StartsWith_APercentSign_IsEscapedOnce()
        => Query(c => c.Name.StartsWith("100%")).ShouldBe("name=100%25*");

    // ----- indexers -----

    [Test]
    public void AnArrayElement_IsRead()
    {
        string[] ids = ["swsh3-136", "base1-4"];
        int i = 1;

        Query(c => c.Id == ids[i]).ShouldBe("id=eq:base1-4");
    }

    [Test]
    public void AListElement_IsRead()
    {
        List<string> names = ["Furret", "Pikachu"];

        Query(c => c.Name == names[1]).ShouldBe("name=eq:Pikachu");
    }

    [Test]
    public void AnIListElement_IsRead()
        => Query(c => c.Hp > Hps[1]).ShouldBe("hp=gt:110");

    /// <summary>Declared as the interface, so the indexer is <c>IList`1.get_Item</c>.</summary>
    public static IList<int> Hps { get; } = [60, 110];

    [Test]
    public void AnIReadOnlyListElement_IsRead()
    {
        IReadOnlyList<string> names = ["Furret"];

        Query(c => c.Name == names[0]).ShouldBe("name=eq:Furret");
    }

    // ----- parameterless instance methods -----

    [Test]
    public void AParameterlessInstanceMethod_IsInvoked()
    {
        string name = "  Furret  ";

        Query(c => c.Name == name.Trim()).ShouldBe("name=eq:Furret");
    }

    [Test]
    public void ChainedCalls_AreInvokedInOrder()
    {
        string[] names = [" furret "];

        Query(c => c.Name.Contains(names[0].ToString().Trim())).ShouldBe("name=furret");
    }

    [Test]
    public void APropertyOfAMethodResult_IsRead()
    {
        string name = "Furret";

        Query(c => c.Hp == name.Trim().Length).ShouldBe("hp=eq:6");
    }

    // ----- conversions -----

    [Test]
    public void AnExplicitNumericCast_IsApplied()
    {
        // C# truncates; dropping the cast would send `3.9`.
        double hp = 3.9;

        Query(c => c.Hp > (int)hp).ShouldBe("hp=gt:3");
    }

    [Test]
    public void AWideningConversion_KeepsTheValue()
    {
        short hp = 70;

        Query(c => c.Hp >= hp).ShouldBe("hp=gte:70");
    }

    // ----- what stays unsupported names the node -----

    [Test]
    public void AStaticMethodCall_IsRejected()
        => Should.Throw<NotSupportedException>(() => Query(c => c.Name == MakeName()))
            .Message.ShouldContain("MakeName()");

    [Test]
    public void AMethodWithArguments_IsRejected()
    {
        string name = "xFurret";

        Should.Throw<NotSupportedException>(() => Query(c => c.Name == name.Substring(1)))
            .Message.ShouldContain("Substring(1)");
    }

    [Test]
    public void AConditional_IsRejected()
    {
        bool flag = true;

        Should.Throw<NotSupportedException>(() => Query(c => c.Name == (flag ? "a" : "b")))
            .Message.ShouldContain("IIF");
    }

    [Test]
    public void ANullReceiver_IsRejectedNamingTheNode()
    {
        string? name = null;

        Should.Throw<NotSupportedException>(() => Query(c => c.Name == name!.Trim()))
            .Message.ShouldContain("Trim()");
    }

    [Test]
    public void ANullArray_IsRejectedNamingTheNode()
    {
        string[]? ids = null;

        Should.Throw<NotSupportedException>(() => Query(c => c.Id == ids![0]))
            .Message.ShouldContain("[0]");
    }

    [Test]
    public void AThrowingMethod_SurfacesTheCallersException()
    {
        Thrower thrower = new();

        Should.Throw<InvalidOperationException>(() => Query(c => c.Name == thrower.Boom()))
            .Message.ShouldBe("boom");
    }

    private static string MakeName() => "Furret";

    private sealed class Thrower
    {
        private readonly string _message = "boom";

        public string Boom() => throw new InvalidOperationException(_message);
    }
}
