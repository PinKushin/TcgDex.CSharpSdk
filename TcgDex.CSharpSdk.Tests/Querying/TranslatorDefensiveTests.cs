namespace TcgDex.Tests.Querying;

using System.Linq.Expressions;
using TcgDex.Querying;

/// <summary>
/// The translator's defensive branches, driven directly.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ExpressionTranslator"/> is generic over the queried model, but
/// <see cref="CardQuery"/> only ever supplies <c>Card</c> — whose properties are
/// strings, numbers and lists. Several rejection paths cannot be reached through
/// that surface, yet they are not dead: they are what a future
/// <c>SetQuery</c> or <c>SerieQuery</c> would hit, and what a model with a
/// boolean or a custom method would hit today.
/// </para>
/// <para>
/// Driving the internal API with a synthetic model exercises them properly.
/// The alternative — leaving them untested and calling them unreachable — would
/// be true only by accident of which model happens to exist.
/// </para>
/// </remarks>
[TestFixture]
public sealed class TranslatorDefensiveTests
{
    /// <summary>
    /// A model with shapes <c>Card</c> does not have: a boolean, and a method
    /// the translator has no mapping for.
    /// </summary>
    private sealed class Probe
    {
        public string Name { get; init; } = "";

        public bool Flag { get; init; }

        public int Score { get; init; }

        /// <summary>A nullable number, so a lifted comparison against null is expressible.</summary>
        public int? Optional { get; init; }

        /// <summary>A nested model, so a method can be called on a direct property.</summary>
        public Probe? Child { get; init; }

        public bool Matches(string other) => Name == other;
    }

    private static string Translate(Expression<Func<Probe, bool>> predicate)
        => string.Join("&", ExpressionTranslator.Translate(predicate).Select(f => f.Render()));

    private static NotSupportedException Rejects(Expression<Func<Probe, bool>> predicate)
        => Should.Throw<NotSupportedException>(() => ExpressionTranslator.Translate(predicate));

    // ----- an OR operand that is neither a comparison nor a method call -----

    [Test]
    public void OrWithAConstantOperand_IsRejected()
    {
        // `|| true` would match everything, quietly discarding the other side.
        NotSupportedException exception = Rejects(p => p.Name == "a" || true);

        exception.Message.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public void OrWithABareBooleanMember_IsRejected()
    {
        // The API has no "field is true" filter, so this cannot be translated.
        NotSupportedException exception = Rejects(p => p.Name == "a" || p.Flag);

        exception.Message.ShouldNotBeNullOrWhiteSpace();
    }

    // ----- a binary operator with no filter equivalent -----

    [Test]
    public void ABitwiseOperatorOnAMember_IsRejected()
    {
        // `&` is a BinaryExpression like `==` is, but there is no operator for
        // it. Reading the node type without checking would emit a filter that
        // silently means something else.
        NotSupportedException exception = Rejects(p => p.Flag & true);

        exception.Message.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public void AnExclusiveOrOnAMember_IsRejected()
        => Rejects(p => p.Flag ^ true).Message.ShouldNotBeNullOrWhiteSpace();

    // ----- an unmapped instance method -----

    [Test]
    public void AMethodCalledOnTheModelItself_IsRejected()
    {
        // The receiver is the lambda parameter rather than a property, so there
        // is no field to filter on.
        Rejects(p => p.Matches("a")).Message.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public void AMethodWithNoFilterEquivalent_IsRejected()
    {
        // One argument, returns bool, called on a direct property — it looks
        // exactly like Contains to the shape check, and only the name
        // distinguishes it. Mapping it by shape alone would emit a filter that
        // means something entirely different.
        NotSupportedException exception = Rejects(p => p.Child!.Matches("a"));

        exception.Message.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public void ARelationalComparisonAgainstNull_IsRejected()
    {
        // Built by hand because `p.Optional > null` is a compiler error here
        // (CS0464, always-false) — but the tree is perfectly constructible, and
        // an expression arriving from anywhere other than C# source could carry
        // it. Only `==` and `!=` against null are presence checks; translating a
        // relational one as `null:` would mean something different entirely.
        ParameterExpression parameter = Expression.Parameter(typeof(Probe), "p");

        Expression<Func<Probe, bool>> predicate = Expression.Lambda<Func<Probe, bool>>(
            Expression.GreaterThan(
                Expression.Property(parameter, nameof(Probe.Optional)),
                Expression.Constant(null, typeof(int?)),
                liftToNull: false,
                method: null),
            parameter);

        Should.Throw<NotSupportedException>(() => ExpressionTranslator.Translate(predicate))
            .Message.ShouldNotBeNullOrWhiteSpace();
    }

    // ----- a value that cannot be read from the tree -----

    [Test]
    public void AMethodCallAsTheComparedValue_IsRejected()
    {
        // Evaluating this would mean invoking caller code during translation,
        // which the translator deliberately never does.
        NotSupportedException exception = Rejects(p => p.Name == MakeName());

        exception.Message.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public void AnArrayIndexAsTheComparedValue_IsRejected()
    {
        string[] names = new[] { "a", "b" };

        Rejects(p => p.Name == names[0]).Message.ShouldNotBeNullOrWhiteSpace();
    }

    // ----- the supported shapes still work on another model -----

    [Test]
    public void TheTranslatorIsNotCardSpecific()
    {
        // Guards the generic contract: a future SetQuery or SerieQuery gets the
        // same behaviour without changes here.
        Translate(p => p.Name == "Furret").ShouldBe("name=eq:Furret");
        Translate(p => p.Score > 10).ShouldBe("score=gt:10");
        Translate(p => p.Name.Contains("fu")).ShouldBe("name=fu");
        Translate(p => p.Name != null).ShouldBe("name=notnull:");
    }

    [Test]
    public void SortFieldNames_AreResolvedOnAnyModel()
        => ExpressionTranslator.SortFieldName<Probe, int>(p => p.Score).ShouldBe("score");

    // ----- value formatting fallbacks -----

    [Test]
    public void AValueThatIsNeitherTextNorFormattable_UsesToString()
    {
        // Falls through every typed branch to the final ToString.
        Probe value = new() { Name = "x" };

        ExpressionTranslator.Format(value).ShouldBe(value.ToString());
    }

    [Test]
    public void AFormattableValue_UsesInvariantCulture()
        => ExpressionTranslator.Format(1234.56m).ShouldBe("1234.56");

    private static string MakeName() => "Furret";
}
