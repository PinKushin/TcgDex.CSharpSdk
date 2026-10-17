namespace TcgDex.Tests.Models;

using TcgDex.Models;

/// <summary>
/// Contract tests for sets, series, list responses and the error body.
/// </summary>
[TestFixture]
public sealed class SetSerieContractTests
{
    [Test]
    public void Deserialize_Set_MapsMetadataAndCards()
    {
        Set set = Fixture.Load<Set>("set-full.json");

        set.Id.ShouldBe("swsh3");
        set.Name.ShouldBe("Darkness Ablaze");
        set.ReleaseDate.ShouldBe(new DateTime(2020, 8, 14));
        set.ReleaseDate!.Value.Kind.ShouldBe(DateTimeKind.Unspecified, "a calendar date, not an instant");
        set.TcgOnline.ShouldBe("DAA");
        set.Abbreviation.ShouldNotBeNull().Official.ShouldBe("DAA");
        set.Serie.ShouldNotBeNull().Id.ShouldBe("swsh");
        set.Legal.ShouldNotBeNull().Expanded.ShouldBeTrue();
    }

    [Test]
    public void Deserialize_Set_MapsFullCardCountBreakdown()
    {
        Set set = Fixture.Load<Set>("set-full.json");

        CardCount count = set.CardCount.ShouldNotBeNull();
        count.Official.ShouldBe(189);
        count.Total.ShouldBe(201);
        count.Normal.ShouldBe(138);
        count.Holo.ShouldBe(69);
        count.Reverse.ShouldBe(157);
        count.FirstEd.ShouldBe(0);
    }

    [Test]
    public void Deserialize_Set_IncludesItsCardsAsBriefs()
    {
        Set set = Fixture.Load<Set>("set-full.json");

        set.Cards.ShouldNotBeEmpty();
        set.Cards.Count.ShouldBe(set.CardCount!.Total);
        set.Cards[0].Id.ShouldStartWith("swsh3-");
    }

    [Test]
    public void Deserialize_Serie_MapsSetsAndBoundaryMarkers()
    {
        Serie serie = Fixture.Load<Serie>("serie-full.json");

        serie.Id.ShouldBe("swsh");
        serie.Name.ShouldBe("Sword & Shield");
        serie.ReleaseDate.ShouldBe(new DateTime(2019, 11, 15));
        serie.FirstSet.ShouldNotBeNull().Id.ShouldBe("swshp");
        serie.LastSet.ShouldNotBeNull();
        serie.Sets.ShouldNotBeEmpty();
    }

    [Test]
    public void Deserialize_CardList_ReturnsBareArrayOfBriefs()
    {
        // List endpoints return a bare array, with no envelope and no total count.
        IReadOnlyList<CardBrief> cards = Fixture.Load<IReadOnlyList<CardBrief>>("list-cards-brief.json");

        cards.ShouldNotBeEmpty();
        cards.ShouldAllBe(c => c.Name == "Furret");
        cards[0].Id.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public void Deserialize_CardList_ToleratesNumericLocalId()
    {
        // CardBrief carries its own LocalId, so hardening Card alone would leave
        // the list endpoint — the one that returns thousands of these at once —
        // still throwing. A converter applied to one model and not its twin is
        // exactly the half-fix a test has to catch.
        string json = Fixture.ReadText("list-cards-brief.json")
            .Replace("\"localId\":\"21\"", "\"localId\":21");

        json.ShouldContain("\"localId\":21", customMessage: "the fixture edit must actually apply");

        IReadOnlyList<CardBrief> cards = Fixture.Parse<IReadOnlyList<CardBrief>>(json);

        cards[0].LocalId.ShouldBe("21");
    }

    [Test]
    public void Deserialize_StringEnumeration_ReturnsScalars()
    {
        IReadOnlyList<string> categories = Fixture.Load<IReadOnlyList<string>>("list-categories.json");

        categories.ShouldBe(["Energy", "Pokemon", "Trainer"], ignoreOrder: true);
    }

    [Test]
    public void Deserialize_NumericEnumeration_ReturnsInts()
    {
        // /retreats, /hp and /dex-ids return numbers where the sibling
        // endpoints return strings.
        IReadOnlyList<int> retreats = Fixture.Load<IReadOnlyList<int>>("list-retreats-int.json");

        retreats.ShouldBe([1, 2, 3, 4, 5]);
    }

    [Test]
    public void Deserialize_NotFoundProblem_MapsProblemDocument()
    {
        TcgDexProblem problem = Fixture.Load<TcgDexProblem>("error-not-found.json");

        problem.Status.ShouldBe(404);
        problem.Type.ShouldBe("https://tcgdex.dev/errors/not-found");
        problem.Method.ShouldBe("GET");
        problem.IsLanguageError.ShouldBeFalse();
        problem.Describe().ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public void Deserialize_LanguageProblem_IsDistinguishableFromNotFound()
    {
        // Both come back as 404, so the status code alone cannot tell them
        // apart — the type URI is the discriminator.
        TcgDexProblem problem = Fixture.Load<TcgDexProblem>("error-bad-language.json");

        problem.Status.ShouldBe(404);
        problem.IsLanguageError.ShouldBeTrue();
        problem.Lang.ShouldBe("zz");
        problem.Describe().ShouldContain("zz");
    }

    // ----- releaseDate is tolerant: anything but a full date becomes null -----

    [TestCase("""{"id":"x","name":"x","releaseDate":"not a date"}""", TestName = "ReleaseDate_Unparsable_IsNull")]
    [TestCase("""{"id":"x","name":"x","releaseDate":"2020-08"}""", TestName = "ReleaseDate_PartialDate_IsNull")]
    [TestCase("""{"id":"x","name":"x","releaseDate":"2020-02-30"}""", TestName = "ReleaseDate_ImpossibleDate_IsNull")]
    [TestCase("""{"id":"x","name":"x","releaseDate":"2020-08-14T10:00:00Z"}""", TestName = "ReleaseDate_DateTime_IsNull")]
    [TestCase("""{"id":"x","name":"x","releaseDate":20200814}""", TestName = "ReleaseDate_Number_IsNull")]
    [TestCase("""{"id":"x","name":"x","releaseDate":{"y":2020}}""", TestName = "ReleaseDate_Object_IsNull")]
    [TestCase("""{"id":"x","name":"x","releaseDate":null}""", TestName = "ReleaseDate_Null_IsNull")]
    [TestCase("""{"id":"x","name":"x"}""", TestName = "ReleaseDate_Absent_IsNull")]
    public void ReleaseDate_AnythingButAFullDate_IsNullNotAThrow(string json)
    {
        Serie serie = Fixture.Parse<Serie>(json);

        serie.ReleaseDate.ShouldBeNull();
        serie.Id.ShouldBe("x", "the rest of the object still deserializes");
    }

    [Test]
    public void ReleaseDate_Set_IsTolerantToo()
        => Fixture.Parse<Set>("""{"id":"x","name":"x","releaseDate":"soon"}""").ReleaseDate.ShouldBeNull();

    [Test]
    public void ReleaseDate_Serializes_AsTheApiSendsIt()
    {
        Serie serie = Fixture.Load<Serie>("serie-full.json");

        string json = JsonSerializer.Serialize(serie, TcgDex.Serialization.TcgDexJsonContext.Default.Serie);

        json.ShouldContain("\"releaseDate\":\"2019-11-15\"");
        Fixture.Parse<Serie>(json).ReleaseDate.ShouldBe(serie.ReleaseDate);
    }

    [Test]
    public void ReleaseDate_Null_IsOmittedWhenSerialized()
        => JsonSerializer.Serialize(
                new Serie { Id = "x", Name = "x" },
                TcgDex.Serialization.TcgDexJsonContext.Default.Serie)
            .ShouldNotContain("releaseDate");
}
