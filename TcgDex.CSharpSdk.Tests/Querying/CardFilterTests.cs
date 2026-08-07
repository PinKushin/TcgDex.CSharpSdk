namespace TcgDex.Tests.Querying;

using TcgDex.Querying;

/// <summary>
/// How <see cref="CardFilter"/> renders itself into GraphQL arguments.
/// </summary>
/// <remarks>
/// <para>
/// Written after mutation testing put this file at 67%. The survivors were not
/// subtle: only <c>Name</c>, <c>Hp</c> and <c>Illustrator</c> were exercised
/// anywhere, so the other twelve fields could each have their line deleted or
/// their GraphQL name replaced with an empty string and the whole suite stayed
/// green.
/// </para>
/// <para>
/// That is a real failure mode rather than a metric artefact. These names are
/// the API's, not ours — <c>regulationMark</c>, <c>trainerType</c>,
/// <c>localId</c> — and a casing slip in any of them produces a query the
/// server rejects or, worse, silently ignores. Nothing in the type system
/// catches it, because they are string literals.
/// </para>
/// </remarks>
[TestFixture]
public sealed class CardFilterTests
{
    [Test]
    public void EveryField_RendersUnderItsApiName_InDeclarationOrder()
    {
        // One filter with everything set, asserted exactly. A per-field test
        // would read better but would not catch a field rendered in the wrong
        // place or a separator that goes missing between two of them.
        var filter = new CardFilter
        {
            Name = "Furret",
            Category = "Pokemon",
            Rarity = "Rare",
            Hp = 110,
            Id = "swsh3-136",
            LocalId = "136",
            DexId = 162,
            Illustrator = "Kagemaru Himeno",
            Stage = "Stage1",
            Suffix = "EX",
            TrainerType = "Item",
            EnergyType = "Basic",
            RegulationMark = "D",
            EvolveFrom = "Sentret",
            Retreat = 1,
        };

        filter.ToGraphQlArguments().ShouldBe(
            """
            name:"Furret",category:"Pokemon",rarity:"Rare",hp:110,id:"swsh3-136",\
            localId:"136",dexId:162,illustrator:"Kagemaru Himeno",stage:"Stage1",\
            suffix:"EX",trainerType:"Item",energyType:"Basic",\
            regulationMark:"D",evolveFrom:"Sentret",retreat:1
            """.Replace("\\\n", string.Empty).Replace("\n", string.Empty));
    }

    [Test]
    public void NoFields_RenderNothing()
    {
        new CardFilter().ToGraphQlArguments().ShouldBeEmpty();
    }

    [TestCase("Name", "name:\"x\"")]
    [TestCase("Category", "category:\"x\"")]
    [TestCase("Rarity", "rarity:\"x\"")]
    [TestCase("Id", "id:\"x\"")]
    [TestCase("LocalId", "localId:\"x\"")]
    [TestCase("Illustrator", "illustrator:\"x\"")]
    [TestCase("Stage", "stage:\"x\"")]
    [TestCase("Suffix", "suffix:\"x\"")]
    [TestCase("TrainerType", "trainerType:\"x\"")]
    [TestCase("EnergyType", "energyType:\"x\"")]
    [TestCase("RegulationMark", "regulationMark:\"x\"")]
    [TestCase("EvolveFrom", "evolveFrom:\"x\"")]
    public void ASingleTextField_RendersAloneWithoutASeparator(string property, string expected)
    {
        // Each field on its own, which the combined test above cannot show:
        // there it is always surrounded by others, so a stray leading comma
        // would be invisible.
        var filter = TextFilterFor(property);

        filter.ToGraphQlArguments().ShouldBe(expected);
    }

    [TestCase("Hp", "hp:7")]
    [TestCase("DexId", "dexId:7")]
    [TestCase("Retreat", "retreat:7")]
    public void ASingleNumericField_RendersUnquoted(string property, string expected)
    {
        // Numbers must not be quoted: the GraphQL schema types these as Int,
        // and a quoted value is rejected outright rather than coerced.
        var filter = NumberFilterFor(property);

        filter.ToGraphQlArguments().ShouldBe(expected);
    }

    private static CardFilter TextFilterFor(string property) => property switch
    {
        "Name" => new CardFilter { Name = "x" },
        "Category" => new CardFilter { Category = "x" },
        "Rarity" => new CardFilter { Rarity = "x" },
        "Id" => new CardFilter { Id = "x" },
        "LocalId" => new CardFilter { LocalId = "x" },
        "Illustrator" => new CardFilter { Illustrator = "x" },
        "Stage" => new CardFilter { Stage = "x" },
        "Suffix" => new CardFilter { Suffix = "x" },
        "TrainerType" => new CardFilter { TrainerType = "x" },
        "EnergyType" => new CardFilter { EnergyType = "x" },
        "RegulationMark" => new CardFilter { RegulationMark = "x" },
        "EvolveFrom" => new CardFilter { EvolveFrom = "x" },
        _ => throw new ArgumentOutOfRangeException(nameof(property), property, "Unmapped text filter."),
    };

    private static CardFilter NumberFilterFor(string property) => property switch
    {
        "Hp" => new CardFilter { Hp = 7 },
        "DexId" => new CardFilter { DexId = 7 },
        "Retreat" => new CardFilter { Retreat = 7 },
        _ => throw new ArgumentOutOfRangeException(nameof(property), property, "Unmapped numeric filter."),
    };
}
