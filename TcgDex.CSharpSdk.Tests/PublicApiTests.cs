#if NET10_0_OR_GREATER

namespace TcgDex.Tests;

using System.IO;
using PublicApiGenerator;

/// <summary>
/// The published surface, compared against a checked-in baseline.
/// </summary>
/// <remarks>
/// <para>
/// Every other test asks whether the SDK behaves correctly. This one asks a
/// different question: <b>has the promise changed?</b> A public member that
/// disappears, gains a parameter, or changes its nullability breaks a
/// consumer's build, and nothing else in this repository would notice — the
/// behaviour tests only exercise the members that still exist.
/// </para>
/// <para>
/// So the baseline is not documentation. It is a diff that has to be
/// deliberately accepted, which turns "I did not realise that was public" into
/// a reviewable line in a commit.
/// </para>
/// <para>
/// <b>Why this rather than the Roslyn public API analyzer.</b> That analyzer is
/// the more common choice and was tried first. It requires the surface to be
/// declared as roughly 1,500 lines with exact nullability annotations, and has
/// no supported way to generate them outside an IDE code fix — <c>dotnet format
/// analyzers</c> does not apply it. This approach generates its own baseline,
/// which is also what Polly and Serilog do.
/// </para>
/// <para>
/// Runs on <c>net10.0</c> only. The package has three surfaces —
/// <c>netstandard2.0</c> cannot expose the <see cref="IAsyncEnumerable{T}"/>
/// members — and one baseline per framework would be three files describing
/// overlapping sets. <c>net10.0</c> is the richest and a superset of the
/// others, so a change to anything shared shows up here.
/// </para>
/// </remarks>
[TestFixture]
public sealed class PublicApiTests
{
    /// <summary>Where the accepted surface lives, relative to this file.</summary>
    private const string BaselineFileName = "PublicApi.approved.cs";

    [Test]
    public void ThePublicSurface_MatchesTheApprovedBaseline()
    {
        string actual = typeof(TcgDexClient).Assembly.GeneratePublicApi(new ApiGeneratorOptions
        {
            // The generated header carries the assembly version, which changes
            // on every version bump and would make the baseline churn for a
            // reason that is not an API change.
            IncludeAssemblyAttributes = false,
        });

        string baselinePath = Path.Combine(SourceDirectory(), BaselineFileName);

        if (!File.Exists(baselinePath))
        {
            File.WriteAllText(baselinePath, actual);

            Assert.Fail(
                $"No baseline existed, so one was written to '{baselinePath}'. " +
                "Review it as the SDK's public promise and commit it — a baseline " +
                "accepted without being read proves nothing.");
        }

        string approved = File.ReadAllText(baselinePath);

        if (string.Equals(Normalise(approved), Normalise(actual), StringComparison.Ordinal))
        {
            return;
        }

        // Written beside the baseline so the diff can be inspected with ordinary
        // tools rather than read out of an assertion message.
        string receivedPath = Path.Combine(SourceDirectory(), "PublicApi.received.cs");
        File.WriteAllText(receivedPath, actual);

        Assert.Fail(
            "The public API has changed.\n\n" +
            $"  approved: {baselinePath}\n" +
            $"  received: {receivedPath}\n\n" +
            "If the change is intended, copy received over approved and commit it — " +
            "that commit is the record of what consumers are promised. If it is not " +
            "intended, something became public that should not have.\n\n" +
            FirstDifference(approved, actual));
    }

    /// <summary>
    /// Line endings are normalised before comparing, so a baseline committed
    /// under one checkout setting does not fail under another.
    /// </summary>
    /// <param name="text">The API text.</param>
    /// <returns>The text with CRLF collapsed to LF and trailing blank lines removed.</returns>
    private static string Normalise(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');

    /// <summary>Reports the first differing line, for a failure message worth reading.</summary>
    /// <param name="approved">The committed baseline.</param>
    /// <param name="actual">The surface as built.</param>
    /// <returns>A description of where the two first diverge.</returns>
    private static string FirstDifference(string approved, string actual)
    {
        string[] expectedLines = Normalise(approved).Split('\n');
        string[] actualLines = Normalise(actual).Split('\n');

        for (int i = 0; i < Math.Min(expectedLines.Length, actualLines.Length); i++)
        {
            if (!string.Equals(expectedLines[i], actualLines[i], StringComparison.Ordinal))
            {
                return $"First difference at line {i + 1}:\n" +
                       $"  approved: {expectedLines[i].Trim()}\n" +
                       $"  received: {actualLines[i].Trim()}";
            }
        }

        return $"The files agree for {Math.Min(expectedLines.Length, actualLines.Length)} lines, " +
               $"then differ in length: approved has {expectedLines.Length}, " +
               $"received has {actualLines.Length}.";
    }

    /// <summary>
    /// The directory holding this source file, so the baseline is written back
    /// to the repository rather than into <c>bin</c>, where it would be lost on
    /// the next clean and could never be committed.
    /// </summary>
    /// <param name="path">Supplied by the compiler; never pass this.</param>
    /// <returns>The directory containing this file.</returns>
    private static string SourceDirectory(
        [System.Runtime.CompilerServices.CallerFilePath] string path = "")
        => Path.GetDirectoryName(path)
           ?? throw new InvalidOperationException($"Could not resolve a directory from '{path}'.");
}

#endif
