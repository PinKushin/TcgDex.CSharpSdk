namespace TcgDex.Tests.Diagnostics;

using TcgDex.Diagnostics;

/// <summary>
/// The version the tracing source reports.
/// </summary>
/// <remarks>
/// It was the literal "0.1.0" through every release, so a trace backend
/// grouping spans by instrumentation version saw one version forever.
/// </remarks>
[TestFixture]
public sealed class ActivityVersionTests
{
    [Test]
    public void SourceVersion_IsTheAssemblyVersion()
    {
        Version version = typeof(TcgDexClient).Assembly.GetName().Version!;

        TcgDexActivity.Source.Version.ShouldBe($"{version.Major}.{version.Minor}.{version.Build}");
    }
}
