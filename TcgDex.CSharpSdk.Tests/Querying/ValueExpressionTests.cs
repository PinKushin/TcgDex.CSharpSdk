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
        => Query(c => c.HitPoints > Hps[1]).ShouldBe("hp=gt:110");

    /// <summary>Declared as the interface, so the indexer is <c>IList`1.get_Item</c>.</summary>
    public static IList<int> Hps { get; } = [60, 110];

    [Test]
    public void AnIReadOnlyListElement_IsRead()
    {
        IReadOnlyList<string> names = ["Furret"];

        Query(c => c.Name == names[0]).ShouldBe("name=eq:Furret");
    }

    [Test]
    public void AStringCharacter_IsRead()
    {
        // The string indexer is String.get_Chars rather than get_Item, so it was
        // rejected while every other indexer was accepted (DECISIONS #33).
        string text = "Furret";

        Query(c => c.Name == text[0].ToString()).ShouldBe("name=eq:F");
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

        Query(c => c.HitPoints == name.Trim().Length).ShouldBe("hp=eq:6");
    }

    // ----- conversions -----

    [Test]
    public void AnExplicitNumericCast_IsApplied()
    {
        // C# truncates; dropping the cast would send `3.9`.
        double hp = 3.9;

        Query(c => c.HitPoints > (int)hp).ShouldBe("hp=gt:3");
    }

    [Test]
    public void AWideningConversion_KeepsTheValue()
    {
        short hp = 70;

        Query(c => c.HitPoints >= hp).ShouldBe("hp=gte:70");
    }

    [Test]
    public void AFloatingValueCastToANullableIntegral_IsTruncated()
    {
        // The target is int?, so truncation has to look through Nullable<T>:
        // int? is not a primitive, and Convert.ChangeType cannot target it.
        double hp = 3.7;

        Query(c => c.HitPoints == (int?)hp).ShouldBe("hp=eq:3");
    }

    [Test]
    public void ABoxingConversionToAnInterface_KeepsTheValue()
    {
        // A value already of the target type passes through. Convert.ChangeType
        // has no route to an interface and would throw for a boxed int.
        int hp = 70;

        Query(c => c.HitPoints == (int?)(IComparable)hp).ShouldBe("hp=eq:70");
    }

    [Test]
    public void ACharWidenedToInt_IsNotTruncatedAsFloating()
    {
        // Only floating values are truncated. A char routed through
        // Convert.ToDecimal throws instead of becoming its code point.
        char letter = 'A';

        Query(c => c.HitPoints == letter).ShouldBe("hp=eq:65");
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
    public void AnOrdinaryMethodNamedGetItem_IsRejected()
    {
        // Only a real indexer (a special-name get_Item) is read with
        // arguments. A plain method that merely shares the name is still a
        // method with arguments.
        Lookalike lookalike = new();

        Should.Throw<NotSupportedException>(() => Query(c => c.Name == lookalike.get_Item(0)))
            .Message.ShouldContain("get_Item(0)");
    }

    [Test]
    public void ASpecialNameMethodThatIsNotAGetter_IsRejected()
    {
        // IsSpecialName alone is not the test: operators, event accessors and
        // setters are special-name too, and only a get_ accessor is an indexer.
        Lookalike lookalike = new();

        Should.Throw<NotSupportedException>(() => Query(c => c.Name == lookalike.Slice(0)))
            .Message.ShouldContain("Slice(0)");
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

    [Test]
    public void AThrowingGetter_SurfacesTheCallersException()
    {
        Thrower thrower = new();

        Should.Throw<InvalidOperationException>(() => Query(c => c.Name == thrower.Bang))
            .Message.ShouldBe("boom");
    }

    // ----- user-defined conversion operators -----

    [Test]
    public void AUserDefinedExplicitOperator_IsInvoked()
    {
        // The operand used to pass through unchanged, so ToString() was sent
        // ("TcgDex...Wrapper") instead of what the operator returns.
        Wrapper wrapper = new("Furret");

        Query(c => c.Name == (string)wrapper).ShouldBe("name=eq:Furret");
    }

    [Test]
    public void ALiftedOperatorOnANullValue_IsNotInvoked()
    {
        // C# lifts S -> int over S?: a null operand yields null without calling
        // the operator. Reflection would pass default(S) instead and send the
        // operator's answer for a value the caller never had.
        Level? none = null;

        Outcome(() => Query(c => c.HitPoints == (int?)none))
            .ShouldBe(Outcome(() => Query(c => c.HitPoints == (int?)null)));
    }

    [Test]
    public void ALiftedOperatorOnAValue_IsInvoked()
    {
        // The counterpart of the null case: a lifted conversion of a value
        // still calls the operator.
        Level? five = new Level(5);

        Query(c => c.HitPoints == (int?)five).ShouldBe("hp=eq:5");
    }

    [Test]
    public void AThrowingOperator_SurfacesTheCallersException()
    {
        Wrapper wrapper = new(null);

        Should.Throw<InvalidOperationException>(() => Query(c => c.Name == (string)wrapper))
            .Message.ShouldBe("empty");
    }

    // ----- casts that cannot succeed have one documented exception type -----

    [Test]
    public void AnOverflowingByteCast_IsRejectedNamingTheNode()
    {
        int value = 300;

        NotSupportedException exception = Should.Throw<NotSupportedException>(() => Query(c => c.HitPoints > (byte)value));
        exception.Message.ShouldContain("Convert(");
        exception.InnerException.ShouldBeOfType<OverflowException>();
    }

    [Test]
    public void AnOverflowingIntCast_IsRejectedNamingTheNode()
    {
        long value = 5_000_000_000L;

        NotSupportedException exception = Should.Throw<NotSupportedException>(() => Query(c => c.HitPoints > (int)value));
        exception.Message.ShouldContain("Convert(");
        exception.InnerException.ShouldBeOfType<OverflowException>();
    }

    [Test]
    public void ANaNCast_IsRejectedNamingTheNode()
    {
        double value = double.NaN;

        NotSupportedException exception = Should.Throw<NotSupportedException>(() => Query(c => c.HitPoints > (int)value));
        exception.Message.ShouldContain("Convert(");
        exception.InnerException.ShouldBeOfType<OverflowException>();
    }

    private static string MakeName() => "Furret";

    private sealed class Thrower
    {
        private readonly string _message = "boom";

        public string Boom() => throw new InvalidOperationException(_message);

        public string Bang => throw new InvalidOperationException(_message);
    }

    /// <summary>A method named like an indexer accessor without being one.</summary>
    private sealed class Lookalike
    {
        private readonly string _name = "Furret";

        public string get_Item(int index) => _name.Substring(index);

        /// <summary>Flagged special-name, like an accessor, but not a get_ one.</summary>
        [System.Runtime.CompilerServices.SpecialName]
        public string Slice(int index) => _name.Substring(index);
    }

    /// <summary>The query string, or the exception type when building it fails.</summary>
    private static string Outcome(Func<string> build)
    {
        try
        {
            return build();
        }
        catch (Exception exception) when (exception is NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return exception.GetType().Name;
        }
    }

    private readonly struct Level(int value)
    {
        public static explicit operator int(Level level)
            => level._value != 0 ? level._value : throw new InvalidOperationException("called with a default Level");

        private readonly int _value = value;
    }

    private sealed class Wrapper(string? value)
    {
        public static explicit operator string(Wrapper wrapper)
            => wrapper.Value ?? throw new InvalidOperationException("empty");

        private string? Value { get; } = value;
    }
}
