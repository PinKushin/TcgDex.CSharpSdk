namespace TcgDex.Tests;

using System.Net.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Each test leg must run against the Microsoft.Extensions line its consumers
/// actually get: 10.x for net10.0, 8.0.x for everything else.
/// </summary>
/// <remarks>
/// The net472 leg exists to execute the netstandard2.0 asset as a .NET Framework
/// or Unity consumer would. It once resolved the 10.x line instead, so that asset
/// was never tested against the 8.0.x dependencies it ships with (DECISIONS #17).
/// </remarks>
[TestFixture]
public sealed class DependencyLineTests
{
#if NET10_0_OR_GREATER
    private const int ExpectedMajor = 10;
#else
    private const int ExpectedMajor = 8;
#endif

    [TestCase(typeof(IOptions<>))]
    [TestCase(typeof(ILogger))]
    [TestCase(typeof(IHttpClientFactory))]
    public void MicrosoftExtensions_ResolvesToTheLineThisTargetShipsWith(Type fromPackage)
    {
        Version? version = fromPackage.Assembly.GetName().Version;

        version.ShouldNotBeNull();
        version.Major.ShouldBe(ExpectedMajor, fromPackage.Assembly.GetName().Name);
    }
}
