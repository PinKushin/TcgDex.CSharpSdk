namespace TcgDex.Tests.Querying;

using TcgDex.Querying;

/// <summary>
/// What the query builder refuses, and what it says when it refuses.
/// </summary>
/// <remarks>
/// The message matters as much as the rejection. A builder that says only
/// "not supported" leaves the caller guessing which part of their predicate was
/// the problem, so each test asserts that the message names something
/// actionable.
/// </remarks>
[TestFixture]
public sealed class QueryRejectionTests
{
    private static CardQuery Query() => new();

    [Test]
    public void MemberOfNestedObject_IsRejected()
    {
        // `set` is filterable but `set.name` is not — the API has no nested
        // field syntax.
        NotSupportedException exception = Should.Throw<NotSupportedException>(
            () => Query().Where(c => c.Set.Name == "Darkness Ablaze"));

        // Naming the nested access is the whole value of the message: `set` on
        // its own is filterable, so "unsupported" without saying which part
        // would look like a contradiction.
        exception.Message.ShouldContain("c.Set.Name");
    }

    [Test]
    public void PropertyOfAProperty_IsRejected()
    {
        NotSupportedException exception = Should.Throw<NotSupportedException>(
            () => Query().Where(c => c.Name.Length == 6));

        exception.Message.ShouldContain("Length");
    }

    [Test]
    public void UnsupportedStringMethod_IsRejected()
    {
        NotSupportedException exception = Should.Throw<NotSupportedException>(
            () => Query().Where(c => c.Name.Trim() == "Furret"));

        // `Trim` is one identifier away from `Contains`, which is supported, so
        // the message has to name the method that was actually rejected.
        exception.Message.ShouldContain("c.Name.Trim()");
    }

    [Test]
    public void OrAcrossDifferentFields_NamesBothFields()
    {
        NotSupportedException exception = Should.Throw<NotSupportedException>(
            () => Query().Where(c => c.Name == "Furret" || c.Rarity == "Common"));

        exception.Message.ShouldContain("name");
        exception.Message.ShouldContain("rarity");

        // And the way out, not just the complaint. Naming the two fields tells
        // the caller what went wrong; "issue one query per field and combine
        // the results" tells them what to do instead, and that half of the
        // message could be deleted with the assertions above still passing.
        exception.Message.ShouldContain("single field");
        exception.Message.ShouldContain("one query per field");
    }

    [Test]
    public void OrWithMismatchedOperators_NamesBothOperators()
    {
        // `name=eq:a|b` requires both sides to use the same operator.
        NotSupportedException exception = Should.Throw<NotSupportedException>(
            () => Query().Where(c => c.Name == "Furret" || c.Name.Contains("Pika")));

        exception.Message.ShouldContain("name");

        // The test is named "NamesBothOperators" but asserted neither. Both are
        // what makes the message actionable — knowing the field is not enough
        // when the problem is that eq and like cannot be mixed.
        exception.Message.ShouldContain("same operator");
        exception.Message.ShouldContain(nameof(QueryOperator.Equal));
        exception.Message.ShouldContain(nameof(QueryOperator.Like));
    }

    [Test]
    public void AnUnsupportedExpression_ListsTheFormsThatAreSupported()
    {
        // The catch-all rejection. Its value is entirely in the list it
        // carries: without it a caller learns only that their predicate is
        // unsupported, with no way to discover what would work short of
        // reading the source.
        NotSupportedException exception = Should.Throw<NotSupportedException>(
            () => Query().Where(c => c.Name.Length == 5));

        exception.Message.ShouldContain("no filter matching");
        exception.Message.ShouldContain("Supported forms are");
        exception.Message.ShouldContain("Contains/StartsWith/EndsWith");
    }

    [Test]
    public void ComparisonBetweenTwoConstants_IsRejected()
    {
        NotSupportedException exception = Should.Throw<NotSupportedException>(
            () => Query().Where(c => "a" == "b"));

        // The message says 'False', not '"a" == "b"' — Roslyn folds a constant
        // comparison before the expression tree is ever built, so the translator
        // never sees a comparison at all. Asserting the folded value is what
        // makes this test honest about the shape it actually rejects; predicting
        // the source text would fail and send someone hunting in the translator
        // for a bug that lives in the compiler's constant folding.
        exception.Message.ShouldContain("'False'");
    }

    [Test]
    public void NegatedComparison_IsRejectedWhereItHasNoEncoding()
    {
        // There is no "not greater than" operator; the caller should invert the
        // comparison themselves rather than get a silently wrong filter.
        NotSupportedException exception = Should.Throw<NotSupportedException>(
            () => Query().Where(c => !(c.Hp > 100)));

        // The inner comparison is what gets named, not the negation — `Hp` is
        // `int?`, so the tree carries a Convert around the constant and
        // asserting the full rendering would pin a compiler detail.
        exception.Message.ShouldContain("c.Hp >");
    }

    [Test]
    public void BooleanMemberWithoutComparison_IsRejected()
    {
        NotSupportedException exception = Should.Throw<NotSupportedException>(
            () => Query().Where(c => c.Attacks.Count > 0));

        exception.Message.ShouldContain("c.Attacks.Count > 0");
    }

    [Test]
    public void SortByNestedMember_IsRejected()
    {
        NotSupportedException exception = Should.Throw<NotSupportedException>(
            () => Query().OrderBy(c => c.Set.Name));

        exception.Message.ShouldContain("direct property");
    }

    [Test]
    public void RejectionMessage_ListsTheSupportedForms()
    {
        // A caller who hits this should be able to fix it without reading the
        // source.
        NotSupportedException exception = Should.Throw<NotSupportedException>(
            () => Query().Where(c => c.Name.Length == 6));

        exception.Message.ShouldContain("Contains");
        exception.Message.ShouldContain("null");
    }

    [Test]
    public void NullPredicate_Throws()
        => Should.Throw<ArgumentNullException>(() => Query().Where(null!));

    [Test]
    public void NullSortSelector_Throws()
        => Should.Throw<ArgumentNullException>(() => Query().OrderBy<string>(null!));

    // ----- captured values of several shapes -----

    [Test]
    public void CapturedField_IsResolved()
    {
        CapturedValues filter = new() { Name = "Furret" };

        Query().Where(c => c.Name == filter.Name).ToQueryString().ShouldBe("name=eq:Furret");
    }

    [Test]
    public void CapturedStaticValue_IsResolved()
        => Query().Where(c => c.Category == CapturedValues.Pokemon).ToQueryString()
            .ShouldBe("category=eq:Pokemon");

    [Test]
    public void CapturedNullableWithValue_IsResolved()
    {
        int? threshold = 90;

        Query().Where(c => c.Hp > threshold).ToQueryString().ShouldBe("hp=gt:90");
    }

    private sealed class CapturedValues
    {
        internal const string Pokemon = "Pokemon";

        internal string Name { get; init; } = "";
    }
}
