namespace TcgDex.Tests.Querying;

using TcgDex.Querying;

/// <summary>
/// Translation from expressions to the API's query syntax.
/// </summary>
/// <remarks>
/// These assert the exact query string, because that string is the contract with
/// the API and every one of these operators was verified against the live
/// service. Asserting anything weaker would let a filter the API cannot parse
/// ship with a green suite.
/// </remarks>
[TestFixture]
public sealed class CardQueryTests
{
    private static CardQuery Query() => new();

    // ----- equality -----

    [Test]
    public void Equality_TranslatesToEqOperator()
        => Query().Where(c => c.Name == "Furret").ToQueryString()
            .ShouldBe("name=eq:Furret");

    [Test]
    public void Inequality_TranslatesToNeqOperator()
        => Query().Where(c => c.Name != "Furret").ToQueryString()
            .ShouldBe("name=neq:Furret");

    [Test]
    public void EqualityAgainstNull_TranslatesToNullOperator()
        => Query().Where(c => c.Effect == null).ToQueryString()
            .ShouldBe("effect=null:");

    [Test]
    public void InequalityAgainstNull_TranslatesToNotNullOperator()
        => Query().Where(c => c.Effect != null).ToQueryString()
            .ShouldBe("effect=notnull:");

    // ----- numeric comparison -----

    [TestCase(true)]
    [TestCase(false)]
    public void GreaterThan_TranslatesToGtOperator(bool reversed)
    {
        // `100 < c.Hp` means the same as `c.Hp > 100`; the operator must flip
        // with the operands rather than being read off the node type.
        CardQuery query = reversed
            ? Query().Where(c => 100 < c.Hp)
            : Query().Where(c => c.Hp > 100);

        query.ToQueryString().ShouldBe("hp=gt:100");
    }

    [Test]
    public void GreaterThanOrEqual_TranslatesToGteOperator()
        => Query().Where(c => c.Hp >= 100).ToQueryString().ShouldBe("hp=gte:100");

    [Test]
    public void LessThan_TranslatesToLtOperator()
        => Query().Where(c => c.Hp < 100).ToQueryString().ShouldBe("hp=lt:100");

    [Test]
    public void LessThanOrEqual_TranslatesToLteOperator()
        => Query().Where(c => c.Hp <= 100).ToQueryString().ShouldBe("hp=lte:100");

    // ----- text matching -----

    [Test]
    public void Contains_TranslatesToLaxistMatch()
        => Query().Where(c => c.Name.Contains("pika")).ToQueryString()
            .ShouldBe("name=pika");

    [Test]
    public void StartsWith_TranslatesToTrailingWildcard()
        => Query().Where(c => c.Name.StartsWith("fu")).ToQueryString()
            .ShouldBe("name=fu*");

    [Test]
    public void EndsWith_TranslatesToLeadingWildcard()
        => Query().Where(c => c.Name.EndsWith("chu")).ToQueryString()
            .ShouldBe("name=*chu");

    [Test]
    public void NegatedContains_TranslatesToNotOperator()
        => Query().Where(c => !c.Name.Contains("pika")).ToQueryString()
            .ShouldBe("name=not:pika");

    // ----- composition -----

    [Test]
    public void AndAlso_BecomesSeparateParameters()
        => Query().Where(c => c.Category == "Pokemon" && c.Hp > 250).ToQueryString()
            .ShouldBe("category=eq:Pokemon&hp=gt:250");

    [Test]
    public void ChainedWhere_BehavesAsAnd()
        => Query()
            .Where(c => c.Category == "Pokemon")
            .Where(c => c.Hp > 250)
            .ToQueryString()
            .ShouldBe("category=eq:Pokemon&hp=gt:250");

    [Test]
    public void OrElseOnOneField_BecomesPipeSeparatedValues()
        => Query().Where(c => c.Name == "Furret" || c.Name == "Pikachu").ToQueryString()
            .ShouldBe("name=eq:Furret|Pikachu");

    [Test]
    public void OrElseAcrossFields_IsRejectedWithAnActionableMessage()
    {
        // The API can only OR within a single field. Silently dropping half the
        // predicate would return wrong data, so this fails loudly instead.
        NotSupportedException exception = Should.Throw<NotSupportedException>(
            () => Query().Where(c => c.Name == "Furret" || c.Hp > 100).ToQueryString());

        exception.Message.ShouldContain("name");
        exception.Message.ShouldContain("hp");
    }

    // ----- escaping -----

    [Test]
    public void Values_AreEscaped()
    {
        // A literal ampersand in a value would otherwise start a new parameter.
        Query().Where(c => c.Name == "Sword & Shield").ToQueryString()
            .ShouldBe("name=eq:Sword%20%26%20Shield");
    }

    [Test]
    public void Wildcards_AreNotEscapedAwayByValueEncoding()
        => Query().Where(c => c.Name.StartsWith("Big Air")).ToQueryString()
            .ShouldBe("name=Big%20Air*");

    // A caller passing a literal "*" — a user typing the wildcard they already
    // know the API understands. The live API answers `name=*` with 200 and
    // every card, and the official JavaScript SDK passes values through
    // untouched, so this is a legitimate match-anything query rather than an
    // error. A single asterisk is *one* wildcard; counting it as both a leading
    // and a trailing one is what used to make this throw.
    [Test]
    public void Where_ReturnsANewQuery_LeavingTheOriginalUntouched()
    {
        // The contract behind every other guarantee here. A fluent builder that
        // mutates in place looks identical at one call site and breaks the
        // moment a caller keeps a base query and branches off it — which is the
        // obvious way to use this, and would fail as a data race rather than as
        // an error anyone could debug.
        CardQuery baseQuery = Query().Where(c => c.Name == "Furret");

        CardQuery narrowed = baseQuery.Where(c => c.Hp > 100);

        baseQuery.ToQueryString().ShouldBe("name=eq:Furret");
        narrowed.ToQueryString().ShouldBe("name=eq:Furret&hp=gt:100");
        narrowed.ShouldNotBeSameAs(baseQuery);
    }

    [Test]
    public void SingleAsterisk_IsOneWildcard_NotTwo()
    {
        // A local rather than a literal: this is how the value actually
        // arrives, and it keeps CA1847 from pushing the call to the char
        // overload, which would exercise a different translator path.
        string search = "*";

        Query().Where(c => c.Name.Contains(search)).ToQueryString()
            .ShouldBe("name=*");
    }

    [Test]
    public void AsteriskOnBothEnds_SurvivesAsBothWildcards()
        => Query().Where(c => c.Name.Contains("**")).ToQueryString()
            .ShouldBe("name=**");

    [Test]
    public void EmptyValue_IsEmittedWithoutBeingMistakenForAWildcard()
    {
        // The wildcard detection indexes the first and last character, so an
        // empty value is the case that would read off the end of the string.
        // `name=eq:` is what the API is sent — an equality match on the empty
        // string, which is a query a caller can legitimately build.
        Query().Where(c => c.Name == string.Empty).ToQueryString()
            .ShouldBe("name=eq:");
    }

    // ----- captured variables -----

    [Test]
    public void CapturedVariable_IsResolvedWithoutCompilingTheExpression()
    {
        // Expression.Compile() is not AOT-safe, so captured values are read
        // from the closure rather than compiled and invoked.
        int minimumHp = 250;

        Query().Where(c => c.Hp > minimumHp).ToQueryString().ShouldBe("hp=gt:250");
    }

    // ----- sorting and pagination -----

    [Test]
    public void OrderBy_EmitsSortParameters()
        => Query().OrderBy(c => c.Name).ToQueryString()
            .ShouldBe("sort:field=name&sort:order=ASC");

    [Test]
    public void OrderByDescending_EmitsDescendingOrder()
        => Query().OrderByDescending(c => c.Name).ToQueryString()
            .ShouldBe("sort:field=name&sort:order=DESC");

    [Test]
    public void Page_EmitsPaginationParameters()
        => Query().Page(2, 50).ToQueryString()
            .ShouldBe("pagination:page=2&pagination:itemsPerPage=50");

    [TestCase(0, 10)]
    [TestCase(-1, 10)]
    [TestCase(1, 0)]
    [TestCase(1, -5)]
    public void Page_RejectsOutOfRangeValues(int page, int itemsPerPage)
        => Should.Throw<ArgumentOutOfRangeException>(() => Query().Page(page, itemsPerPage));

    [Test]
    public void FullQuery_CombinesFiltersSortAndPaginationInOrder()
        => Query()
            .Where(c => c.Name.Contains("Pikachu"))
            .Where(c => c.Hp > 100)
            .OrderByDescending(c => c.Name)
            .Page(2, 50)
            .ToQueryString()
            .ShouldBe("name=Pikachu&hp=gt:100&sort:field=name&sort:order=DESC&pagination:page=2&pagination:itemsPerPage=50");

    [Test]
    public void EmptyQuery_ProducesNoQueryString()
        => Query().ToQueryString().ShouldBeEmpty();

    [Test]
    public void QueryString_NeverContainsTheInventedQParameter()
    {
        // Regression guard: `?q=` is not a TCGdex parameter. Filters are
        // top-level query parameters.
        string queryString = Query()
            .Where(c => c.Name == "Furret")
            .Where(c => c.Hp > 100)
            .Page(1, 10)
            .ToQueryString();

        queryString.ShouldNotContain("q=");
    }

    // ----- unsupported expressions -----

    [Test]
    public void UnsupportedExpression_NamesTheOffendingPredicate()
    {
        // The API has no such operator, so translating this is impossible.
        // The message must say so rather than producing a silently wrong filter.
        NotSupportedException exception = Should.Throw<NotSupportedException>(
            () => Query().Where(c => c.Name.Length > 5).ToQueryString());

        exception.Message.ShouldContain("Length");
    }

    [Test]
    public void UnknownProperty_IsRejected()
    {
        // BaseDamage is computed client-side and has no API counterpart.
        NotSupportedException exception = Should.Throw<NotSupportedException>(
            () => Query().Where(c => c.Set.Name == "x").ToQueryString());

        exception.Message.ShouldNotBeNullOrWhiteSpace();
    }
}
