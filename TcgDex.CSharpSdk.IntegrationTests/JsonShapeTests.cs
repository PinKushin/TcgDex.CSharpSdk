namespace TcgDex.IntegrationTests;

/// <summary>
/// The shape-comparison engine the fixture drift check is built on.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately <em>not</em> derived from <c>LiveApiFixture</c>, so these carry
/// no <c>Integration</c> category and run on every push rather than only in the
/// weekly live job. <see cref="JsonShape"/> is a pure function over two strings;
/// gating its tests behind a network schedule would mean the comparison engine
/// every drift verdict depends on was itself only checked once a week.
/// </para>
/// <para>
/// What matters here is the failure direction. A comparison that misses a real
/// change reports green, and a comparison that invents one trains everybody to
/// ignore the check — so both are tested.
/// </para>
/// </remarks>
[TestFixture]
public sealed class JsonShapeTests
{
    // ----- the union of heterogeneous array elements -----

    [Test]
    public void Describe_UnionOfKinds_DoesNotDependOnElementOrder()
    {
        // `attacks[].damage` really is Number on one card and String on another,
        // so a path legitimately holds two kinds. Building that union by
        // appending in encounter order makes the fingerprint order-dependent:
        // the same document with its elements swapped describes as
        // "Number|String" instead of "String|Number", and Compare then reports a
        // retype that never happened.
        //
        // A spurious breaking failure is worse than a missed one here, because
        // the drift check runs unattended and the only available response to a
        // red weekly run is to go looking for a change that does not exist.
        IReadOnlyDictionary<string, string> forward =
            JsonShape.Describe("""{"a":[{"d":1},{"d":"1+"}]}""");

        IReadOnlyDictionary<string, string> reversed =
            JsonShape.Describe("""{"a":[{"d":"1+"},{"d":1}]}""");

        forward["a[].d"].ShouldBe(reversed["a[].d"]);
    }

    [Test]
    public void Compare_TheSameDocumentWithReorderedElements_ReportsNothing()
    {
        // The end-to-end version of the above: this is what the drift check
        // actually asks, and it must not answer "breaking".
        IReadOnlyDictionary<string, string> recorded =
            JsonShape.Describe("""{"a":[{"d":1},{"d":"1+"}]}""");

        IReadOnlyDictionary<string, string> live =
            JsonShape.Describe("""{"a":[{"d":"1+"},{"d":1}]}""");

        (IReadOnlyList<string> breaking, IReadOnlyList<string> additive) = JsonShape.Compare(recorded, live);

        breaking.ShouldBeEmpty();
        additive.ShouldBeEmpty();
    }

    [Test]
    public void Describe_NullOnOneElementAndTypedOnAnother_IsOneOptionalField()
    {
        IReadOnlyDictionary<string, string> shape =
            JsonShape.Describe("""{"a":[{"d":null},{"d":"x"}]}""");

        shape["a[].d"].ShouldBe("String", "null is absence, not a second type");
    }

    // ----- the comparisons the drift check depends on -----

    [Test]
    public void Compare_AFieldThatDisappeared_IsBreaking()
    {
        (IReadOnlyList<string> breaking, _) = JsonShape.Compare(
            JsonShape.Describe("""{"id":"x","hp":1}"""),
            JsonShape.Describe("""{"id":"x"}"""));

        breaking.Count.ShouldBe(1);
        breaking[0].ShouldContain("hp");
    }

    [Test]
    public void Compare_AFieldThatChangedType_IsBreaking()
    {
        // The localId case: documented as "String or Number", and a retype is
        // what would break deserialization of a required property.
        (IReadOnlyList<string> breaking, _) = JsonShape.Compare(
            JsonShape.Describe("""{"localId":"136"}"""),
            JsonShape.Describe("""{"localId":136}"""));

        breaking.Count.ShouldBe(1);
        breaking[0].ShouldContain("localId");
    }

    [Test]
    public void Compare_ANewField_IsAdditiveNotBreaking()
    {
        (IReadOnlyList<string> breaking, IReadOnlyList<string> additive) = JsonShape.Compare(
            JsonShape.Describe("""{"id":"x"}"""),
            JsonShape.Describe("""{"id":"x","pricing":{"avg":1}}"""));

        breaking.ShouldBeEmpty();
        additive.ShouldNotBeEmpty("a field the API started serving is the drift worth knowing about");
    }

    [Test]
    public void Compare_IdenticalDocuments_ReportNothing()
    {
        // The negative control. Without it, a Compare that returned everything
        // as a difference would still pass every other test in this file.
        const string Json = """{"id":"x","set":{"cardCount":{"official":1}},"types":["Grass"]}""";

        (IReadOnlyList<string> breaking, IReadOnlyList<string> additive) = JsonShape.Compare(
            JsonShape.Describe(Json),
            JsonShape.Describe(Json));

        breaking.ShouldBeEmpty();
        additive.ShouldBeEmpty();
    }

    [Test]
    public void Describe_NestedPaths_AreDotted()
    {
        IReadOnlyDictionary<string, string> shape =
            JsonShape.Describe("""{"set":{"cardCount":{"official":189}}}""");

        shape["set.cardCount.official"].ShouldBe("Number");
    }
}
