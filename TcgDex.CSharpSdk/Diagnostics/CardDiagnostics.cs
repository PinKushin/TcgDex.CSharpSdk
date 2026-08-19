namespace TcgDex.Diagnostics;

using System.Globalization;
using Microsoft.Extensions.Logging;
using TcgDex.Models;

/// <summary>
/// Reports malformed fields on a deserialized <see cref="Card"/> to the log.
/// </summary>
/// <remarks>
/// <para>
/// The SDK reads what the API sends rather than rejecting it, so a card whose
/// data is broken upstream — a nameless attack, which <c>2017sm-5</c> ships —
/// deserializes to a card with a null where a name should be. That null is
/// honest: it is what the API returned. But a caller staring at a blank name
/// cannot tell whether the SDK dropped it or the API never had it, so this says,
/// once and in the channel built for it, that the API produced the hole.
/// </para>
/// <para>
/// <b>It never touches the data.</b> The alternative — defaulting the name to a
/// sentinel like <c>"(unnamed)"</c> — would let the SDK fabricate content a
/// caller could not distinguish from a real name, and would collide the day a
/// real attack is named that. Diagnostics belong in the log, not in the field.
/// </para>
/// <para>
/// <b>Cost when nobody is listening is one branch.</b> The whole scan is behind
/// a single <see cref="ILogger.IsEnabled"/> check, so with warnings off it does
/// not walk the collections or build a message — the same discipline the
/// <c>LoggerMessage</c> generator applies per call, extended to the loop.
/// </para>
/// </remarks>
internal static class CardDiagnostics
{
    /// <summary>
    /// Logs a warning for each malformed field on <paramref name="card"/>.
    /// </summary>
    /// <param name="logger">The client's logger.</param>
    /// <param name="card">A freshly deserialized card.</param>
    internal static void WarnOnAnomalies(ILogger logger, Card card)
    {
        // One branch when warnings are disabled: no walk, no string building.
        if (!logger.IsEnabled(LogLevel.Warning))
        {
            return;
        }

        for (int i = 0; i < card.Attacks.Count; i++)
        {
            if (card.Attacks[i].Name is null)
            {
                logger.MalformedCardData(card.Id, Ordinal("attack", i));
            }
        }

        for (int i = 0; i < card.Abilities.Count; i++)
        {
            if (card.Abilities[i].Name is null)
            {
                logger.MalformedCardData(card.Id, Ordinal("ability", i));
            }
        }
    }

    /// <summary>Builds a 1-based "attack 2 has no name" detail string.</summary>
    /// <remarks>
    /// Concatenation rather than an interpolation handler: the
    /// <c>string.Create(IFormatProvider, …)</c> overload is net6+ and this
    /// assembly still targets <c>netstandard2.0</c>. Only reached on a malformed
    /// card with warnings enabled, so the allocation is not a hot path.
    /// </remarks>
    private static string Ordinal(string kind, int zeroBasedIndex)
        => kind + " " + (zeroBasedIndex + 1).ToString(CultureInfo.InvariantCulture) + " has no name";
}
