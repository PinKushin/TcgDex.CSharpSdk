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
        set.ReleaseDate.ShouldBe("2020-08-14");
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
        serie.ReleaseDate.ShouldBe("2019-11-15");
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
}
