namespace TcgDex;

/// <summary>
/// The language codes the TCGdex API accepts.
/// </summary>
/// <remarks>
/// This list is not guesswork: requesting an unsupported code makes the API
/// enumerate the valid set in its error body, and these are exactly those
/// values. Note that GraphQL ignores language entirely and always answers in
/// English.
/// </remarks>
public static class TcgDexLanguages
{
    /// <summary>English — the API's default.</summary>
    public const string English = "en";

    /// <summary>French.</summary>
    public const string French = "fr";

    /// <summary>Spanish.</summary>
    public const string Spanish = "es";

    /// <summary>Spanish (Mexico).</summary>
    public const string SpanishMexico = "es-mx";

    /// <summary>Italian.</summary>
    public const string Italian = "it";

    /// <summary>Portuguese.</summary>
    public const string Portuguese = "pt";

    /// <summary>Portuguese (Brazil).</summary>
    public const string PortugueseBrazil = "pt-br";

    /// <summary>Portuguese (Portugal).</summary>
    public const string PortuguesePortugal = "pt-pt";

    /// <summary>German.</summary>
    public const string German = "de";

    /// <summary>Dutch.</summary>
    public const string Dutch = "nl";

    /// <summary>Polish.</summary>
    public const string Polish = "pl";

    /// <summary>Russian.</summary>
    public const string Russian = "ru";

    /// <summary>Japanese.</summary>
    public const string Japanese = "ja";

    /// <summary>Korean.</summary>
    public const string Korean = "ko";

    /// <summary>Chinese (Traditional).</summary>
    public const string ChineseTraditional = "zh-tw";

    /// <summary>Indonesian.</summary>
    public const string Indonesian = "id";

    /// <summary>Thai.</summary>
    public const string Thai = "th";

    /// <summary>Chinese (Simplified).</summary>
    public const string ChineseSimplified = "zh-cn";

    /// <summary>Every supported language code.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        English, French, Spanish, SpanishMexico, Italian, Portuguese,
        PortugueseBrazil, PortuguesePortugal, German, Dutch, Polish, Russian,
        Japanese, Korean, ChineseTraditional, Indonesian, Thai, ChineseSimplified,
    ];

    /// <summary>
    /// Whether the API supports <paramref name="language"/>. Comparison ignores
    /// case, matching how the API treats the path segment.
    /// </summary>
    /// <param name="language">The language code to test, such as <c>"pt-br"</c>.</param>
    /// <returns><see langword="true"/> when the code is supported.</returns>
    public static bool IsSupported(string? language)
        => language is not null
           && All.Contains(language, StringComparer.OrdinalIgnoreCase);
}
