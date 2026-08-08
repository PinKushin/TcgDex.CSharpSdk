namespace TcgDex.IntegrationTests;

using TcgDex.Models;

/// <summary>
/// Query translation and pagination, verified against the live API.
/// </summary>
/// <remarks>
/// Unit tests prove the builder produces a given string. Only these prove the
/// API agrees with what that string means.
/// </remarks>
[TestFixture]
public sealed class QueryAndPagingTests : LiveApiFixture
{
    [Test]
    public async Task ExactMatch_ReturnsOnlyExactMatches()
    {
        IReadOnlyList<CardBrief> cards = await Client.Cards.ListAsync(
            new CardQuery().Where(c => c.Name == "Furret"),
            Timeout);

        cards.ShouldNotBeEmpty();
        cards.ShouldAllBe(c => c.Name == "Furret");
    }

    [Test]
    public async Task LooseMatch_ReturnsSubstringMatches()
    {
        IReadOnlyList<CardBrief> cards = await Client.Cards.ListAsync(
            new CardQuery().Where(c => c.Name.Contains("furret")).Page(1, 30),
            Timeout);

        cards.ShouldNotBeEmpty();
        cards.ShouldAllBe(c => c.Name.Contains("furret", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public async Task NotEqual_ExcludesTheValue()
    {
        IReadOnlyList<CardBrief> cards = await Client.Cards.ListAsync(
            new CardQuery().Where(c => c.Name != "Furret").Page(1, 30),
            Timeout);

        cards.ShouldNotBeEmpty();
        cards.ShouldAllBe(c => c.Name != "Furret");
    }

    [Test]
    public async Task StartsWith_AnchorsToTheStart()
    {
        IReadOnlyList<CardBrief> cards = await Client.Cards.ListAsync(
            new CardQuery().Where(c => c.Name.StartsWith("Fu")).Page(1, 30),
            Timeout);

        cards.ShouldNotBeEmpty();
        cards.ShouldAllBe(c => c.Name.StartsWith("Fu", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public async Task BareWildcard_IsAcceptedAndMatchesAnything()
    {
        // A lone "*" used to throw out of the query builder before it could
        // reach the network. The unit tests prove the string is now `name=*`;
        // only this proves the API agrees that it means "match anything"
        // rather than rejecting it.
        string search = "*";

        IReadOnlyList<CardBrief> cards = await Client.Cards.ListAsync(
            new CardQuery().Where(c => c.Name.Contains(search)).Page(1, 30),
            Timeout);

        // Deliberately no assertion on which cards come back — that is exactly
        // the volatile live data this suite must not depend on. A full page
        // proves the filter was accepted and did not narrow to nothing.
        cards.Count.ShouldBe(30);
    }

    [Test]
    public async Task EndsWith_AnchorsToTheEnd()
    {
        IReadOnlyList<CardBrief> cards = await Client.Cards.ListAsync(
            new CardQuery().Where(c => c.Name.EndsWith("chu")).Page(1, 30),
            Timeout);

        cards.ShouldNotBeEmpty();
        cards.ShouldAllBe(c => c.Name.EndsWith("chu", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public async Task OrWithinOneField_ReturnsBothAlternatives()
    {
        IReadOnlyList<CardBrief> cards = await Client.Cards.ListAsync(
            new CardQuery().Where(c => c.Name == "Furret" || c.Name == "Sentret").Page(1, 60),
            Timeout);

        List<string> names = cards.Select(c => c.Name).Distinct().ToList();
        names.ShouldBe(["Furret", "Sentret"], ignoreOrder: true);
    }

    [Test]
    public async Task AndAcrossFields_NarrowsResults()
    {
        // Briefs carry no category, so this is verified by fetching one back.
        IReadOnlyList<CardBrief> cards = await Client.Cards.ListAsync(
            new CardQuery()
                .Where(c => c.Category == CardCategories.Trainer)
                .Where(c => c.TrainerType == "Tool")
                .Page(1, 5),
            Timeout);

        cards.ShouldNotBeEmpty();

        Card? card = await Client.Cards.GetAsync(cards[0].Id, Timeout);
        card.ShouldNotBeNull().Category.ShouldBe(CardCategories.Trainer);
        card.TrainerType.ShouldBe("Tool");
    }

    [Test]
    public async Task NumericComparison_IsHonoured()
    {
        IReadOnlyList<CardBrief> cards = await Client.Cards.ListAsync(
            new CardQuery().Where(c => c.Hp > 300).Page(1, 10),
            Timeout);

        // A full page. An ignored filter and an honoured one both return
        // results, so "not empty" cannot tell them apart — the page size shows
        // the request was understood, and the HP check below shows it was
        // applied.
        cards.Count.ShouldBe(10, "far more than ten cards have over 300 HP");

        Card? card = await Client.Cards.GetAsync(cards[0].Id, Timeout);
        card.ShouldNotBeNull().Hp.ShouldNotBeNull().ShouldBeGreaterThan(300);
    }

    [Test]
    public async Task Sorting_ChangesResultOrder()
    {
        IReadOnlyList<CardBrief> ascending = await Client.Cards.ListAsync(
            new CardQuery().Where(c => c.Name == "Furret").OrderBy(c => c.Id).Page(1, 20),
            Timeout);

        IReadOnlyList<CardBrief> descending = await Client.Cards.ListAsync(
            new CardQuery().Where(c => c.Name == "Furret").OrderByDescending(c => c.Id).Page(1, 20),
            Timeout);

        ascending.ShouldNotBeEmpty();
        ascending.Select(c => c.Id).ShouldBe(descending.Select(c => c.Id).Reverse());
    }

    // ----- pagination boundaries -----

    [Test]
    public async Task PageSizeOfOne_ReturnsExactlyOne()
    {
        IReadOnlyList<CardBrief> cards = await Client.Cards.ListAsync(new CardQuery().Page(1, 1), Timeout);

        cards.Count.ShouldBe(1);
    }

    [Test]
    public async Task SuccessivePages_DoNotOverlap()
    {
        IReadOnlyList<CardBrief> first = await Client.Cards.ListAsync(new CardQuery().Page(1, 5), Timeout);
        IReadOnlyList<CardBrief> second = await Client.Cards.ListAsync(new CardQuery().Page(2, 5), Timeout);

        first.Count.ShouldBe(5);
        second.Count.ShouldBe(5);
        first.Select(c => c.Id).Intersect(second.Select(c => c.Id)).ShouldBeEmpty();
    }

    [Test]
    public async Task PageBeyondTheEnd_ReturnsEmptyRatherThanFailing()
    {
        // The API exposes no total count, so a short or empty page is the only
        // signal that results are exhausted.
        IReadOnlyList<CardBrief> cards = await Client.Cards.ListAsync(
            new CardQuery().Where(c => c.Name == "Furret").Page(500, 100),
            Timeout);

        cards.ShouldBeEmpty();
    }

    [Test]
    public async Task ShortPage_SignalsTheEndOfResults()
    {
        CardQuery query = new CardQuery().Where(c => c.Name == "Furret").Page(1, 100);

        IReadOnlyList<CardBrief> cards = await Client.Cards.ListAsync(query, Timeout);

        // Far fewer than 100 Furrets exist, so this page is the last one.
        cards.Count.ShouldBeLessThan(100);
        cards.ShouldNotBeEmpty();
    }

    // ----- error paths -----

    [Test]
    public async Task MissingSet_ReturnsNull()
    {
        Set? set = await Client.Sets.GetAsync("no-such-set-999", Timeout);

        set.ShouldBeNull();
    }

    [Test]
    public async Task MissingSerie_ReturnsNull()
    {
        Serie? serie = await Client.Series.GetAsync("no-such-serie-999", Timeout);

        serie.ShouldBeNull();
    }

    [Test]
    public async Task FilterMatchingNothing_ReturnsEmptyNotNull()
    {
        IReadOnlyList<CardBrief> cards = await Client.Cards.ListAsync(
            new CardQuery().Where(c => c.Name == "ThisPokemonDoesNotExist"),
            Timeout);

        cards.ShouldNotBeNull();
        cards.ShouldBeEmpty();
    }

    [Test]
    public void CancelledRequest_ThrowsOperationCanceled()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Should.ThrowAsync<OperationCanceledException>(
            async () => await Client.Cards.GetAsync("swsh3-136", cts.Token));
    }
}
