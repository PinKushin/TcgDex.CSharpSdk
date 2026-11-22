namespace TcgDex.Tests;

using System.Net.Http;
using System.Reflection;
using System.Runtime.Versioning;
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
/// The netstandard2.0 lane on net10.0 (DECISIONS #37 row 19) gets 8.0.x for the same reason.
/// </remarks>
[TestFixture]
public sealed class DependencyLineTests
{
#if NET10_0_OR_GREATER && !TCGDEX_NETSTANDARD_ASSET
    private const int ExpectedMajor = 10;
#else
    private const int ExpectedMajor = 8;
#endif

#if NETFRAMEWORK || TCGDEX_NETSTANDARD_ASSET
    private const string ExpectedAsset = ".NETStandard,Version=v2.0";
#elif NET10_0_OR_GREATER
    private const string ExpectedAsset = ".NETCoreApp,Version=v10.0";
#else
    private const string ExpectedAsset = ".NETCoreApp,Version=v8.0";
#endif

    [Test]
    public void EachLeg_LoadsTheLibraryAssetItIsMeantToTest()
    {
        // The control for every other test on this leg: the net472 leg and the
        // netstandard2.0 lane (DECISIONS #37 row 19) exist only to execute the
        // netstandard2.0 asset, and a reference that quietly resolved another
        // asset would leave both passing while testing nothing new.
        typeof(TcgDexClient).Assembly
            .GetCustomAttribute<TargetFrameworkAttribute>()
            .ShouldNotBeNull()
            .FrameworkName
            .ShouldBe(ExpectedAsset);
    }

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
