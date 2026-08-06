namespace TcgDex.IntegrationTests;

using System.Text;
using System.Text.Json;

/// <summary>
/// The structural signature of a JSON document: which keys exist and what type
/// each holds, ignoring values.
/// </summary>
/// <remarks>
/// <para>
/// Values are deliberately excluded. Prices move daily and <c>updated</c>
/// changes whenever a record is touched, so comparing values would fail
/// constantly and teach everyone to ignore the check. Shape is the part the SDK
/// actually depends on.
/// </para>
/// <para>
/// Arrays are summarised by the <em>union</em> of their elements' shapes. One
/// card's <c>variants_detailed</c> entries differ from another's — some carry a
/// stamp, some do not — and treating only the first element as representative
/// would report drift that is really just heterogeneity.
/// </para>
/// </remarks>
internal static class JsonShape
{
    /// <summary>
    /// Describes a document as a map of dotted path to observed type.
    /// </summary>
    /// <param name="json">The document to describe.</param>
    /// <returns>Paths such as <c>set.cardCount.official</c> mapped to <c>Number</c>.</returns>
    internal static IReadOnlyDictionary<string, string> Describe(string json)
    {
        using var document = JsonDocument.Parse(json);

        var shape = new SortedDictionary<string, string>(StringComparer.Ordinal);
        Walk(document.RootElement, prefix: string.Empty, shape);

        return shape;
    }

    private static void Walk(JsonElement element, string prefix, IDictionary<string, string> shape)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var path = prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}";
                    Record(path, property.Value, shape);
                    Walk(property.Value, path, shape);
                }

                break;

            case JsonValueKind.Array:
                // Union across elements: a field present on only some entries
                // still belongs to the shape.
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, $"{prefix}[]", shape);
                }

                break;

            default:
                break;
        }
    }

    private static void Record(string path, JsonElement value, IDictionary<string, string> shape)
    {
        var kind = Describe(value.ValueKind);

        if (!shape.TryGetValue(path, out var existing))
        {
            shape[path] = kind;
            return;
        }

        if (existing == kind)
        {
            return;
        }

        // A field that is null on one array element and typed on another is one
        // optional field, not two conflicting ones.
        if (existing == "Null")
        {
            shape[path] = kind;
        }
        else if (kind != "Null")
        {
            shape[path] = $"{existing}|{kind}";
        }
    }

    private static string Describe(JsonValueKind kind)
        => kind switch
        {
            JsonValueKind.Object => "Object",
            JsonValueKind.Array => "Array",
            JsonValueKind.String => "String",
            JsonValueKind.Number => "Number",
            JsonValueKind.True or JsonValueKind.False => "Boolean",
            JsonValueKind.Null => "Null",
            _ => "Undefined",
        };

    /// <summary>
    /// Compares a recorded shape against a live one.
    /// </summary>
    /// <param name="recorded">The shape committed to the repository.</param>
    /// <param name="live">The shape fetched just now.</param>
    /// <returns>
    /// A description of every breaking difference, and separately of every
    /// additive one.
    /// </returns>
    /// <remarks>
    /// Removals and type changes are breaking: the models and every offline test
    /// are written against the recorded shape. New fields are not — they mean
    /// the API grew something the SDK could model, which is worth knowing but
    /// must not fail a build.
    /// </remarks>
    internal static (IReadOnlyList<string> Breaking, IReadOnlyList<string> Additive) Compare(
        IReadOnlyDictionary<string, string> recorded,
        IReadOnlyDictionary<string, string> live)
    {
        var breaking = new List<string>();
        var additive = new List<string>();

        foreach (var (path, recordedKind) in recorded)
        {
            if (!live.TryGetValue(path, out var liveKind))
            {
                breaking.Add($"removed: '{path}' was {recordedKind}, now absent");
                continue;
            }

            if (recordedKind == liveKind)
            {
                continue;
            }

            // Null on one side means the field exists but had no value in that
            // sample — an optional field, not a type change.
            if (recordedKind.Contains("Null", StringComparison.Ordinal)
                || liveKind.Contains("Null", StringComparison.Ordinal))
            {
                continue;
            }

            breaking.Add($"retyped: '{path}' was {recordedKind}, now {liveKind}");
        }

        foreach (var (path, liveKind) in live)
        {
            if (!recorded.ContainsKey(path))
            {
                additive.Add($"added: '{path}' ({liveKind})");
            }
        }

        return (breaking, additive);
    }

    /// <summary>Formats differences for a test failure message.</summary>
    /// <param name="fixture">The fixture file name.</param>
    /// <param name="source">Where it was fetched from.</param>
    /// <param name="differences">The differences to list.</param>
    /// <returns>A readable multi-line report.</returns>
    internal static string Report(string fixture, string source, IReadOnlyList<string> differences)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"'{fixture}' no longer matches {source}:");

        foreach (var difference in differences)
        {
            builder.AppendLine($"    {difference}");
        }

        builder.AppendLine();
        builder.AppendLine("    Every offline test is written against this recording, so the models");
        builder.AppendLine("    and docs/api-info.md likely need updating too. Refresh with");
        builder.AppendLine("    scripts/Update-Fixtures.ps1 once the SDK has been adjusted.");

        return builder.ToString();
    }
}
