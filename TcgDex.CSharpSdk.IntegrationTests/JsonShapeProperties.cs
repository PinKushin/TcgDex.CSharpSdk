namespace TcgDex.IntegrationTests;

using System.Globalization;
using System.Text;
using CsCheck;

/// <summary>
/// Algebraic properties of <see cref="JsonShape"/>, the comparison engine every
/// fixture-drift verdict rests on.
/// </summary>
/// <remarks>
/// <para>
/// This file exists because of a bug that a property would have found
/// immediately and that example tests did not find for months. A path whose kind
/// varies across array elements — <c>attacks[].damage</c> is a number on one
/// card and a string on another — had its union built by appending in encounter
/// order, so the same document described as <c>Number|String</c> or
/// <c>String|Number</c> depending on which element came first, and the
/// comparison read that difference as a retype.
/// </para>
/// <para>
/// Stated as a property it is one line — <em>the description of a document does
/// not depend on the order of its array elements</em> — and generation finds it
/// without anyone having thought of the case.
/// </para>
/// <para>
/// No <c>Integration</c> category: these need no network, so they run on every
/// push rather than in the weekly live job.
/// </para>
/// </remarks>
[TestFixture]
public sealed class JsonShapeProperties
{
    /// <summary>
    /// One JSON value per generated element, drawn from the kinds that actually
    /// collide in TCGdex responses: numbers against strings, and nulls that must
    /// be absorbed rather than treated as a second type.
    /// </summary>
    private static readonly Gen<string> Values =
        Gen.OneOf(
            Gen.Const("null"),
            Gen.Int[0, 999].Select(n => n.ToString(CultureInfo.InvariantCulture)),
            Gen.Int[0, 99].Select(n => $"\"{n}+\""),
            Gen.Const("true"),
            Gen.Const("{\"n\":1}"));

    /// <summary>
    /// An array of single-field objects, which is the shape where a union forms.
    /// </summary>
    private static readonly Gen<string[]> Documents = Values.Array[1, 8];

    private static string Document(IEnumerable<string> values)
    {
        StringBuilder builder = new("{\"a\":[");
        builder.Append(string.Join(",", values.Select(v => $"{{\"d\":{v}}}")));
        builder.Append("]}");

        return builder.ToString();
    }

    [Test]
    public void Describe_DoesNotDependOnElementOrder()
    {
        // The bug, as a property. Reversal is enough to expose an
        // order-dependent fold; no shuffle is needed, and reversal shrinks to a
        // readable counter-example.
        Documents.Sample(values =>
        {
            string forward = Document(values);
            string reversed = Document(values.Reverse());

            IReadOnlyDictionary<string, string> a = JsonShape.Describe(forward);
            IReadOnlyDictionary<string, string> b = JsonShape.Describe(reversed);

            return a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out string? kind) && kind == pair.Value);
        });
    }

    [Test]
    public void Compare_IsReflexive()
    {
        // A document never differs from itself. Without this, a Compare that
        // reported every path as a difference would satisfy every test that only
        // checks a real difference is caught.
        Documents.Sample(values =>
        {
            IReadOnlyDictionary<string, string> shape = JsonShape.Describe(Document(values));

            (IReadOnlyList<string> breaking, IReadOnlyList<string> additive) = JsonShape.Compare(shape, shape);

            return breaking.Count == 0 && additive.Count == 0;
        });
    }

    [Test]
    public void Compare_IsSymmetricInWhatItNotices()
    {
        // Whatever the drift is, both directions must see *something*. A
        // comparison that noticed a field appearing but not the same field
        // disappearing would let a removal through in whichever direction the
        // drift check happens to run.
        Gen.Select(Documents, Documents).Sample((left, right) =>
        {
            IReadOnlyDictionary<string, string> a = JsonShape.Describe(Document(left));
            IReadOnlyDictionary<string, string> b = JsonShape.Describe(Document(right));

            (IReadOnlyList<string> forwardBreaking, IReadOnlyList<string> forwardAdditive) = JsonShape.Compare(a, b);
            (IReadOnlyList<string> backBreaking, IReadOnlyList<string> backAdditive) = JsonShape.Compare(b, a);

            bool forwardNoticed = forwardBreaking.Count > 0 || forwardAdditive.Count > 0;
            bool backNoticed = backBreaking.Count > 0 || backAdditive.Count > 0;

            return forwardNoticed == backNoticed;
        });
    }
}
