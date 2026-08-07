namespace TcgDex.Querying;

using System.Text;

/// <summary>
/// An equality-only filter for the GraphQL card search.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately narrower than <see cref="CardQuery"/>. GraphQL's
/// <c>CardsFilters</c> input takes typed scalars, so <c>hp</c> accepts a number
/// and nothing else — passing <c>"gt:100"</c> fails with
/// <c>Int cannot represent non-integer value</c>. There are no ranges, no
/// wildcards, and no null checks on this path.
/// </para>
/// <para>
/// Use <see cref="CardQuery"/> against REST when you need those. Use this when
/// you want full card detail for many cards in a single round trip.
/// </para>
/// </remarks>
public sealed record CardFilter
{
    /// <summary>Exact card name.</summary>
    public string? Name { get; init; }

    /// <summary>Card category: <c>Pokemon</c>, <c>Trainer</c> or <c>Energy</c>.</summary>
    public string? Category { get; init; }

    /// <summary>Exact rarity.</summary>
    public string? Rarity { get; init; }

    /// <summary>Exact hit points.</summary>
    public int? Hp { get; init; }

    /// <summary>Card identifier.</summary>
    public string? Id { get; init; }

    /// <summary>Card number within its set.</summary>
    public string? LocalId { get; init; }

    /// <summary>National Pokédex number.</summary>
    public int? DexId { get; init; }

    /// <summary>Illustrator name.</summary>
    public string? Illustrator { get; init; }

    /// <summary>Evolution stage.</summary>
    public string? Stage { get; init; }

    /// <summary>Name suffix, such as <c>EX</c>.</summary>
    public string? Suffix { get; init; }

    /// <summary>Trainer subtype.</summary>
    public string? TrainerType { get; init; }

    /// <summary>Energy category.</summary>
    public string? EnergyType { get; init; }

    /// <summary>Regulation mark.</summary>
    public string? RegulationMark { get; init; }

    /// <summary>The Pokémon this card evolves from.</summary>
    public string? EvolveFrom { get; init; }

    /// <summary>Retreat cost.</summary>
    public int? Retreat { get; init; }

    /// <summary>
    /// Renders the filter as a GraphQL argument list, or an empty string when
    /// nothing is set.
    /// </summary>
    /// <returns>Text such as <c>name:"Furret",hp:110</c>.</returns>
    internal string ToGraphQlArguments()
    {
        var builder = new StringBuilder();

        AppendText(builder, "name", Name);
        AppendText(builder, "category", Category);
        AppendText(builder, "rarity", Rarity);
        AppendNumber(builder, "hp", Hp);
        AppendText(builder, "id", Id);
        AppendText(builder, "localId", LocalId);
        AppendNumber(builder, "dexId", DexId);
        AppendText(builder, "illustrator", Illustrator);
        AppendText(builder, "stage", Stage);
        AppendText(builder, "suffix", Suffix);
        AppendText(builder, "trainerType", TrainerType);
        AppendText(builder, "energyType", EnergyType);
        AppendText(builder, "regulationMark", RegulationMark);
        AppendText(builder, "evolveFrom", EvolveFrom);
        AppendNumber(builder, "retreat", Retreat);

        return builder.ToString();
    }

    private static void AppendText(StringBuilder builder, string field, string? value)
    {
        if (value is null)
        {
            return;
        }

        Separate(builder);
        builder.Append(field).Append(':').Append(Quote(value));
    }

    private static void AppendNumber(StringBuilder builder, string field, int? value)
    {
        if (value is null)
        {
            return;
        }

        Separate(builder);
        builder.Append(field).Append(':')
            .Append(value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void Separate(StringBuilder builder)
    {
        if (builder.Length > 0)
        {
            builder.Append(',');
        }
    }

    /// <summary>
    /// Quotes a value as a GraphQL string literal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two separate jobs. The quote and backslash cases are the security ones:
    /// a card name containing either would otherwise break out of the literal
    /// and change the query being sent, and nothing else in a string can.
    /// </para>
    /// <para>
    /// The control characters are a correctness one. The GraphQL grammar
    /// forbids them raw inside a string, so passing one through unescaped turns
    /// a caller's odd input into a server-side syntax error rather than a clean
    /// result. Backspace and form feed have dedicated escapes; everything else
    /// below U+0020 goes out as \uXXXX.
    /// </para>
    /// </remarks>
    private static string Quote(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');

        foreach (var character in value)
        {
            switch (character)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;

                default:
                    // Uppercase hex, four digits: the grammar accepts either
                    // case, and fixing one keeps the wire output assertable.
                    if (character < ' ')
                    {
                        builder.Append("\\u").Append(((int)character).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }
}
