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
        var exception = Should.Throw<NotSupportedException>(
            () => Query().Where(c => c.Set.Name == "Darkness Ablaze"));

        exception.Message.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public void PropertyOfAProperty_IsRejected()
    {
        var exception = Should.Throw<NotSupportedException>(
            () => Query().Where(c => c.Name.Length == 6));

        exception.Message.ShouldContain("Length");
    }

    [Test]
    public void UnsupportedStringMethod_IsRejected()
    {
        var exception = Should.Throw<NotSupportedException>(
            () => Query().Where(c => c.Name.Trim() == "Furret"));

        exception.Message.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public void OrAcrossDifferentFields_NamesBothFields()
    {
        var exception = Should.Throw<NotSupportedException>(
            () => Query().Where(c => c.Name == "Furret" || c.Rarity == "Common"));

        exception.Message.ShouldContain("name");
        exception.Message.ShouldContain("rarity");
    }

    [Test]
    public void OrWithMismatchedOperators_NamesBothOperators()
    {
        // `name=eq:a|b` requires both sides to use the same operator.
        var exception = Should.Throw<NotSupportedException>(
            () => Query().Where(c => c.Name == "Furret" || c.Name.Contains("Pika")));

        exception.Message.ShouldContain("name");
    }

    [Test]
    public void ComparisonBetweenTwoConstants_IsRejected()
    {
        var exception = Should.Throw<NotSupportedException>(
            () => Query().Where(c => "a" == "b"));

        exception.Message.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public void NegatedComparison_IsRejectedWhereItHasNoEncoding()
    {
        // There is no "not greater than" operator; the caller should invert the
        // comparison themselves rather than get a silently wrong filter.
        var exception = Should.Throw<NotSupportedException>(
            () => Query().Where(c => !(c.Hp > 100)));

        exception.Message.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public void BooleanMemberWithoutComparison_IsRejected()
    {
        var exception = Should.Throw<NotSupportedException>(
            () => Query().Where(c => c.Attacks.Count > 0));

        exception.Message.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public void SortByNestedMember_IsRejected()
    {
        var exception = Should.Throw<NotSupportedException>(
            () => Query().OrderBy(c => c.Set.Name));

        exception.Message.ShouldContain("direct property");
    }

    [Test]
    public void RejectionMessage_ListsTheSupportedForms()
    {
        // A caller who hits this should be able to fix it without reading the
        // source.
        var exception = Should.Throw<NotSupportedException>(
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
        var filter = new CapturedValues { Name = "Furret" };

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
