namespace TcgDex.Querying;

/// <summary>
/// The comparison operators the TCGdex API accepts in a filter.
/// </summary>
/// <remarks>
/// This is the complete set, verified against the live service. There is no
/// operator for anything else, which is why predicates that do not map onto one
/// of these are rejected rather than approximated.
/// </remarks>
public enum QueryOperator
{
    /// <summary>Loose substring match — the API's default when no prefix is given.</summary>
    Like,

    /// <summary>Loose substring exclusion (<c>not:</c>).</summary>
    NotLike,

    /// <summary>Exact match (<c>eq:</c>).</summary>
    Equal,

    /// <summary>Exact exclusion (<c>neq:</c>).</summary>
    NotEqual,

    /// <summary>Greater than (<c>gt:</c>).</summary>
    GreaterThan,

    /// <summary>Greater than or equal (<c>gte:</c>).</summary>
    GreaterThanOrEqual,

    /// <summary>Less than (<c>lt:</c>).</summary>
    LessThan,

    /// <summary>Less than or equal (<c>lte:</c>).</summary>
    LessThanOrEqual,

    /// <summary>Field is absent (<c>null:</c>).</summary>
    Null,

    /// <summary>Field is present (<c>notnull:</c>).</summary>
    NotNull,
}

/// <summary>
/// A single filter: one field, one operator, and the values it matches.
/// </summary>
/// <param name="Field">The API field name, for example <c>hp</c>.</param>
/// <param name="Operator">The comparison to apply.</param>
/// <param name="Values">
/// The values to match. More than one means OR, which the API supports only
/// within a single field.
/// </param>
internal sealed record QueryFilter(string Field, QueryOperator Operator, IReadOnlyList<string> Values)
{
    /// <summary>
    /// Renders the filter as a <c>field=operator:value</c> query parameter.
    /// </summary>
    /// <returns>The encoded parameter.</returns>
    internal string Render()
    {
        string prefix = Operator switch
        {
            QueryOperator.Equal => "eq:",
            QueryOperator.NotEqual => "neq:",
            QueryOperator.GreaterThan => "gt:",
            QueryOperator.GreaterThanOrEqual => "gte:",
            QueryOperator.LessThan => "lt:",
            QueryOperator.LessThanOrEqual => "lte:",
            QueryOperator.NotLike => "not:",
            QueryOperator.Null => "null:",
            QueryOperator.NotNull => "notnull:",
            _ => string.Empty,
        };

        if (Operator is QueryOperator.Null or QueryOperator.NotNull)
        {
            return $"{Field}={prefix}";
        }

        // Values are escaped, but the operator prefix, the `|` that separates
        // OR alternatives, and any `*` wildcard are structural and stay literal.
        string values = string.Join("|", Values.Select(EscapeValue));

        return $"{Field}={prefix}{values}";
    }

    private static string EscapeValue(string value)
    {
        // Indexed rather than StartsWith('*')/EndsWith('*'): the char overloads
        // and the range indexers below are all post-netstandard2.0, and a
        // direct character comparison is both portable and exactly what those
        // overloads do.
        bool leadingWildcard = value.Length > 0 && value[0] == '*';

        // `> 1`, so a lone "*" counts as one wildcard rather than as both a
        // leading and a trailing one. Counting it twice left nothing to strip
        // and threw ArgumentOutOfRangeException out of the substring below —
        // for a query the API answers with 200 and every card.
        bool trailingWildcard = value.Length > 1 && value[value.Length - 1] == '*';

        string core = value;
        if (leadingWildcard)
        {
            core = core.Substring(1);
        }

        if (trailingWildcard)
        {
            core = core.Substring(0, core.Length - 1);
        }

        string escaped = Uri.EscapeDataString(core);

        return (leadingWildcard ? "*" : string.Empty)
             + escaped
             + (trailingWildcard ? "*" : string.Empty);
    }
}
