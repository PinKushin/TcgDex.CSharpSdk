namespace TcgDex.Tests.Http;

using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TcgDex;
using TcgDex.Models;
using TcgDex.Querying;
using TcgDex.Tests.Diagnostics;

/// <summary>
/// A GraphQL search whose result holds a card the server could not fully resolve (DECISIONS #49).
/// </summary>
/// <remarks>
/// The live shape, recorded 2026-11-21: the schema types <c>AttacksListItem.name</c> as non-null, some cards
/// have unnamed attacks, and the server answers HTTP 200 with every card, <c>null</c> in place of each unnamed
/// attack, and one error per null whose <c>path</c> is <c>["cards", index, "attacks", n, "name"]</c>. Fourteen
/// cards did this across a full scan. REST types the name as optional, so the card is fetched again there.
/// </remarks>
[TestFixture]
public sealed class GraphQLRepairTests
{
    private const string NullName = "Cannot return null for non-nullable field AttacksListItem.name.";

    private const string Furret = """
        {"id":"swsh3-136","name":"Furret","category":"Pokemon","localId":"136","set":{"id":"swsh3","name":"Darkness Ablaze"}}
        """;

    /// <summary>The GraphQL view of <c>2017sm-5</c>: its second attack has no name, so the server nulls it.</summary>
    private const string BrokenPikachu = """
        {"id":"2017sm-5","name":"Pikachu","category":"Pokemon","localId":"5",
         "attacks":[{"name":"Thudner Wave","cost":["Lightning"]},null],
         "set":{"id":"2017sm","name":"McDonald's Collection 2017"}}
        """;

    private static string Search(string cards, string errors)
        => $$"""{"data":{"cards":[{{cards}}]},"errors":[{{errors}}]}""";

    private static string Error(string message, string path)
        => $$"""{"message":"{{message}}","path":{{path}}}""";

    private static Task<IReadOnlyList<Card>> SearchAsync(RecordingHandler handler, TcgDexOptions? options = null, ILoggerFactory? logs = null)
        => new TcgDexClient(new HttpClient(handler), options ?? new TcgDexOptions(), logs).Cards
            .SearchDetailedAsync(new CardFilter { Name = "Pikachu" }, CancellationToken.None);

    [Test]
    public async Task ACardWithAnUnnamedAttack_IsFetchedAgainOverRest_InPlace()
    {
        RecordingHandler handler = new RecordingHandler()
            .RespondWith(HttpStatusCode.OK, Search($"{Furret},{BrokenPikachu}", Error(NullName, """["cards",1,"attacks",1,"name"]""")))
            .RespondWithJsonFile(HttpStatusCode.OK, "card-nameless-attack.json");

        IReadOnlyList<Card> cards = await SearchAsync(handler);

        cards.Select(c => c.Id).ShouldBe(["swsh3-136", "2017sm-5"]);

        // REST's copy: both attacks present, the second with no name, rather than GraphQL's null.
        Card pikachu = cards[1];
        pikachu.Attacks.Count.ShouldBe(2);
        pikachu.Attacks[0].Name.ShouldBe("Thudner Wave");
        pikachu.Attacks[1].ShouldNotBeNull().Name.ShouldBeNull();
        pikachu.Attacks[1].Cost.ShouldBe(["Lightning", "Colorless", "Colorless"]);

        handler.Requests.Count.ShouldBe(2);
        handler.Requests[1].Method.ShouldBe(HttpMethod.Get);
        handler.Requests[1].RequestUri!.ToString().ShouldBe("https://api.tcgdex.net/v2/en/cards/2017sm-5");
    }

    [Test]
    public async Task TheRefetch_IsInEnglish_WhateverLanguageTheClientUses()
    {
        // GraphQL has no language, so every other card in the result is English.
        RecordingHandler handler = new RecordingHandler()
            .RespondWith(HttpStatusCode.OK, Search(BrokenPikachu, Error(NullName, """["cards",0,"attacks",1,"name"]""")))
            .RespondWithJsonFile(HttpStatusCode.OK, "card-nameless-attack.json");

        await SearchAsync(handler, new TcgDexOptions { Language = TcgDexLanguages.French });

        handler.Requests[1].RequestUri!.ToString().ShouldBe("https://api.tcgdex.net/v2/en/cards/2017sm-5");
    }

    [Test]
    public async Task TheRefetchedCard_DropsTheFieldsGraphQLNeverFills()
    {
        // The control: over REST this fixture carries every one of them.
        Card rest = Fixture.Load<Card>("card-with-boosters.json");
        rest.Pricing.ShouldNotBeNull();
        rest.Updated.ShouldNotBeNull();
        rest.DetailedVariants.ShouldNotBeEmpty();
        rest.Boosters.ShouldNotBeEmpty();

        const string Broken = """
            {"id":"A4-139","name":"Ho-Oh","category":"Pokemon","localId":"139","attacks":[null],"set":{"id":"A4","name":"Wisdom of Sea and Sky"}}
            """;

        RecordingHandler handler = new RecordingHandler()
            .RespondWith(HttpStatusCode.OK, Search(Broken, Error(NullName, """["cards",0,"attacks",0,"name"]""")))
            .RespondWithJsonFile(HttpStatusCode.OK, "card-with-boosters.json");

        Card card = (await SearchAsync(handler)).ShouldHaveSingleItem();

        card.Pricing.ShouldBeNull();
        card.Updated.ShouldBeNull();
        card.ThirdParty.ShouldBeNull();
        card.DetailedVariants.ShouldBeEmpty();
        card.Boosters.ShouldBeEmpty();
        card.ShouldBe(rest with
        {
            Pricing = null,
            Updated = null,
            ThirdParty = null,
            DetailedVariants = [],
            Boosters = [],
        });
    }

    [Test]
    public async Task TheRefetchedCard_DropsItsCardLevelMarketplaceIds()
    {
        // A separate fixture: the one above has no card-level thirdParty, so it cannot show this field cleared.
        Fixture.Load<Card>("card-damage-int.json").ThirdParty.ShouldNotBeNull();

        const string Broken = """
            {"id":"xy1-1","name":"Venusaur EX","category":"Pokemon","localId":"1","attacks":[null],"set":{"id":"xy1","name":"XY"}}
            """;

        RecordingHandler handler = new RecordingHandler()
            .RespondWith(HttpStatusCode.OK, Search(Broken, Error(NullName, """["cards",0,"attacks",0,"name"]""")))
            .RespondWithJsonFile(HttpStatusCode.OK, "card-damage-int.json");

        (await SearchAsync(handler)).ShouldHaveSingleItem().ThirdParty.ShouldBeNull();
    }

    [Test]
    public async Task TwoBrokenCards_AreEachFetchedOnce_InResultOrder_AndLandInTheirOwnPlaces()
    {
        const string BrokenFurret = """
            {"id":"swsh3-136","name":"Furret","category":"Pokemon","localId":"136","attacks":[null],"set":{"id":"swsh3","name":"Darkness Ablaze"}}
            """;
        const string Trainer = """
            {"id":"x-1","name":"Potion","category":"Trainer","localId":"1","set":{"id":"x","name":"X"}}
            """;

        // The errors arrive last card first; the refetches go in result order.
        string errors = Error(NullName, """["cards",2,"attacks",0,"name"]""") + "," + Error(NullName, """["cards",0,"attacks",1,"name"]""");
        RecordingLogger log = new(LogLevel.Trace);
        RecordingHandler handler = new RecordingHandler()
            .RespondWith(HttpStatusCode.OK, Search($"{BrokenPikachu},{Trainer},{BrokenFurret}", errors))
            .RespondWithJsonFile(HttpStatusCode.OK, "card-nameless-attack.json")
            .RespondWithJsonFile(HttpStatusCode.OK, "card-pokemon-full.json");

        IReadOnlyList<Card> cards = await SearchAsync(handler, logs: log.Factory);

        handler.Requests.Skip(1).Select(r => r.RequestUri!.ToString()).ShouldBe(
        [
            "https://api.tcgdex.net/v2/en/cards/2017sm-5",
            "https://api.tcgdex.net/v2/en/cards/swsh3-136",
        ]);
        cards.Select(c => c.Id).ShouldBe(["2017sm-5", "x-1", "swsh3-136"]);
        cards[0].Attacks.Count.ShouldBe(2);
        cards[0].Attacks.ShouldAllBe(a => a != null, "the broken card also has two attacks, one null");
        cards[2].Attacks.ShouldNotBeEmpty();
        cards[2].Attacks.ShouldAllBe(a => a != null);
        log.Entries.Where(e => e.EventId == 1303).ShouldHaveSingleItem().Message
            .ShouldBe("TCGdex GraphQL could not resolve 2 card(s) (2017sm-5, swsh3-136); they were fetched again over REST");
    }

    [Test]
    public async Task SeveralErrorsInOneCard_RefetchItOnce()
    {
        const string BothNull = """
            {"id":"2017sm-5","name":"Pikachu","category":"Pokemon","localId":"5","attacks":[null,null],"set":{"id":"2017sm","name":"McDonald's Collection 2017"}}
            """;

        RecordingHandler handler = new RecordingHandler()
            .RespondWith(HttpStatusCode.OK, Search(BothNull, Error(NullName, """["cards",0,"attacks",0,"name"]""") + "," + Error(NullName, """["cards",0,"attacks",1,"name"]""")))
            .RespondWithJsonFile(HttpStatusCode.OK, "card-nameless-attack.json");

        // The broken card also has two attacks, both null, so the count alone would not show the repair.
        IReadOnlyList<Attack?> attacks = (await SearchAsync(handler)).ShouldHaveSingleItem().Attacks;
        attacks.Count.ShouldBe(2);
        attacks.ShouldAllBe(a => a != null);

        handler.Requests.Count.ShouldBe(2);
    }

    [Test]
    public async Task TheRepair_IsLoggedAsAWarning_NamingTheCards()
    {
        RecordingLogger log = new(LogLevel.Trace);
        RecordingHandler handler = new RecordingHandler()
            .RespondWith(HttpStatusCode.OK, Search(BrokenPikachu, Error(NullName, """["cards",0,"attacks",1,"name"]""")))
            .RespondWithJsonFile(HttpStatusCode.OK, "card-nameless-attack.json");

        await SearchAsync(handler, logs: log.Factory);

        LogEntry entry = log.Entries.Where(e => e.EventId == 1303).ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldBe("TCGdex GraphQL could not resolve 1 card(s) (2017sm-5); they were fetched again over REST");
        log.Entries.ShouldNotContain(e => e.EventId == 1301, "nothing failed, so no error is logged");
        log.Entries.ShouldContain(e => e.EventId == 1400, "the REST fetch reports the nameless attack itself");
    }

    // ----- what is still a failure -----

    [TestCase(NullName, """["cards",0]""", TestName = "A path that ends at the card")]
    [TestCase(NullName, """["cards",5,"attacks",1,"name"]""", TestName = "An index past the end")]
    [TestCase(NullName, """["cards",1,"attacks",1,"name"]""", TestName = "An index one past the end")]
    [TestCase(NullName, """[0,0,"attacks",1,"name"]""", TestName = "A path that does not start with a name")]
    [TestCase(NullName, """["cards",-1,"attacks",1,"name"]""", TestName = "A negative index")]
    [TestCase(NullName, """["sets",0,"attacks",1,"name"]""", TestName = "A path outside cards")]
    [TestCase(NullName, """["cards","0","attacks",1,"name"]""", TestName = "An index that is not a number")]
    [TestCase(NullName, "null", TestName = "No path")]
    [TestCase("Something else went wrong.", """["cards",0,"attacks",1,"name"]""", TestName = "An error that is not a null")]
    public void AnErrorThatIsNotANullInsideACard_StillThrows_WithoutARefetch(string message, string path)
    {
        RecordingHandler handler = new RecordingHandler()
            .RespondWith(HttpStatusCode.OK, Search(BrokenPikachu, Error(message, path)))
            .RespondWithJsonFile(HttpStatusCode.OK, "card-nameless-attack.json");

        TcgDexApiException error = Should.ThrowAsync<TcgDexApiException>(() => SearchAsync(handler)).Result;

        error.Kind.ShouldBe(TcgDexErrorKind.GraphQLError);
        error.Message.ShouldContain(message);
        handler.Requests.Count.ShouldBe(1);
    }

    [Test]
    public void AnErrorPointingAtANullCard_StillThrows_WithoutARefetch()
    {
        // With the card itself null there is no id to fetch it by.
        RecordingHandler handler = new RecordingHandler()
            .RespondWith(HttpStatusCode.OK, Search($"{Furret},null", Error(NullName, """["cards",1,"attacks",0,"name"]""")))
            .RespondWithJsonFile(HttpStatusCode.OK, "card-nameless-attack.json");

        Should.ThrowAsync<TcgDexApiException>(() => SearchAsync(handler)).Result.Kind.ShouldBe(TcgDexErrorKind.GraphQLError);
        handler.Requests.Count.ShouldBe(1);
    }

    [TestCase(" ")]
    [TestCase(".")]
    [TestCase("..")]
    public void AnErrorInACardWhoseIdCannotBeAPathSegment_StillThrows_WithoutARefetch(string id)
    {
        // The REST path rejects these with an ArgumentException naming `id`, which the caller never passed.
        string unfetchable = $$$"""
            {"id":"{{{id}}}","name":"Pikachu","category":"Pokemon","localId":"5","attacks":[null],"set":{"id":"2017sm","name":"McDonald's Collection 2017"}}
            """;
        RecordingHandler handler = new RecordingHandler()
            .RespondWith(HttpStatusCode.OK, Search(unfetchable, Error(NullName, """["cards",0,"attacks",0,"name"]""")));

        Should.ThrowAsync<TcgDexApiException>(() => SearchAsync(handler)).Result.Kind.ShouldBe(TcgDexErrorKind.GraphQLError);
        handler.Requests.Count.ShouldBe(1);
    }

    [Test]
    public void AnErrorWithNoMessage_StillThrows_WithoutARefetch()
    {
        RecordingHandler handler = new RecordingHandler()
            .RespondWith(HttpStatusCode.OK, Search(BrokenPikachu, """{"path":["cards",0,"attacks",1,"name"]}"""));

        Should.ThrowAsync<TcgDexApiException>(() => SearchAsync(handler)).Result.Kind.ShouldBe(TcgDexErrorKind.GraphQLError);
        handler.Requests.Count.ShouldBe(1);
    }

    [Test]
    public void OneUnrepairableError_FailsTheSearch_EvenBesideARepairableOne()
    {
        string errors = Error(NullName, """["cards",0,"attacks",1,"name"]""") + "," + Error("Something else went wrong.", """["cards",0,"name"]""");
        RecordingHandler handler = new RecordingHandler()
            .RespondWith(HttpStatusCode.OK, Search(BrokenPikachu, errors))
            .RespondWithJsonFile(HttpStatusCode.OK, "card-nameless-attack.json");

        TcgDexApiException error = Should.ThrowAsync<TcgDexApiException>(() => SearchAsync(handler)).Result;

        error.Message.ShouldContain("Something else went wrong.");
        handler.Requests.Count.ShouldBe(1, "nothing is refetched when the search fails anyway");
    }

    [Test]
    public void ACardRestCannotFind_FailsTheSearch()
    {
        RecordingHandler handler = new RecordingHandler()
            .RespondWith(HttpStatusCode.OK, Search(BrokenPikachu, Error(NullName, """["cards",0,"attacks",1,"name"]""")))
            .RespondWithJsonFile(HttpStatusCode.NotFound, "error-not-found.json");

        TcgDexApiException error = Should.ThrowAsync<TcgDexApiException>(() => SearchAsync(handler)).Result;

        error.Kind.ShouldBe(TcgDexErrorKind.GraphQLError);
        error.Message.ShouldContain(NullName);
        error.Message.ShouldContain("card '2017sm-5', fetched again over REST, was not found there");
        handler.Requests.Count.ShouldBe(2);
    }

    [Test]
    public void AFailedRefetch_ReportsTheRestFailure()
    {
        RecordingHandler handler = new RecordingHandler()
            .RespondWith(HttpStatusCode.OK, Search(BrokenPikachu, Error(NullName, """["cards",0,"attacks",1,"name"]""")))
            .RespondWith(HttpStatusCode.BadGateway, "{}");

        Should.ThrowAsync<TcgDexApiException>(() => SearchAsync(handler)).Result.Kind.ShouldBe(TcgDexErrorKind.HttpStatus);
    }
}
