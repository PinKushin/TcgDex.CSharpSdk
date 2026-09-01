namespace TcgDex.Tests;

using System.Threading;
using TcgDex;

/// <summary>
/// The failover configuration surface.
/// </summary>
/// <remarks>
/// The first test here is the most important one in the file. Failover trades a
/// failed request for extra requests, so a build that shipped it enabled by
/// default would add load to a free, community-run API for every consumer who
/// never asked for it — and it would do so most on the day the service was
/// already struggling. Nothing else in the suite would notice.
/// </remarks>
[TestFixture]
public sealed class TcgDexOptionsFailoverTests
{
    [Test]
    public void FailoverIsOffUntilItIsAskedFor()
    {
        TcgDexOptions options = new();

        options.FailoverEndpoints.ShouldBeEmpty();
    }

    [Test]
    public void TheDefaults_DivideTheRequestBudgetRatherThanExtendIt()
    {
        // Three attempts at ten seconds is exactly the thirty-second request
        // Timeout, so the ceiling a caller asked for is never exceeded. If
        // either value moves, this is the assertion that says the relationship
        // was reconsidered rather than forgotten.
        TcgDexOptions options = new();

        options.FailoverAttemptTimeout.ShouldBe(TimeSpan.FromSeconds(10));
        options.Timeout.ShouldBe(TimeSpan.FromSeconds(30));
        options.FailoverCooldown.ShouldBe(TimeSpan.FromMinutes(5));
    }

    [Test]
    public void UseFailover_WithNoArguments_TakesEveryOfficialNode()
    {
        TcgDexOptions options = new();

        options.UseFailover();

        options.FailoverEndpoints.Select(endpoint => endpoint.ToString()).ShouldBe(
        [
            "https://api.eu1.tcgdex.net/v2/",
            "https://api.eu2.tcgdex.net/v2/",
            "https://api.eu3.tcgdex.net/v2/",
            "https://api.na1.tcgdex.net/v2/",
            "https://api.na2.tcgdex.net/v2/",
            "https://api.as1.tcgdex.net/v2/",
        ]);
    }

    [Test]
    public void UseFailover_WithMirrors_KeepsTheOrderGiven()
    {
        TcgDexOptions options = new();

        options.UseFailover(TcgDexMirror.Na1, TcgDexMirror.Eu2);

        options.FailoverEndpoints.Select(endpoint => endpoint.ToString()).ShouldBe(
        [
            "https://api.na1.tcgdex.net/v2/",
            "https://api.eu2.tcgdex.net/v2/",
        ]);
    }

    [Test]
    public void UseFailover_AcceptsAnUnofficialEndpoint()
    {
        // The durable half of the feature: a server TCGdex does not run can only
        // be reached this way, whatever they add upstream.
        TcgDexOptions options = new();

        options.UseFailover(new Uri("https://tcgdex.example.dev/v2/"));

        options.FailoverEndpoints.Single().ToString().ShouldBe("https://tcgdex.example.dev/v2/");
    }

    [Test]
    public void UseFailover_DoesNotAliasTheCallersArray()
    {
        // Otherwise a caller mutating their array afterwards would silently
        // redirect traffic on a client already in use.
        Uri[] endpoints = [new("https://one.example/v2/")];
        TcgDexOptions options = new();

        options.UseFailover(endpoints);
        endpoints[0] = new Uri("https://two.example/v2/");

        options.FailoverEndpoints.Single().ToString().ShouldBe("https://one.example/v2/");
    }

    [Test]
    public void UseFailover_ReturnsTheSameInstance()
    {
        TcgDexOptions options = new();

        options.UseFailover(TcgDexMirror.Eu2).ShouldBeSameAs(options);
    }

    [Test]
    public void UseFailover_WithNoEndpoints_Throws()
    {
        TcgDexOptions options = new();

        Should.Throw<ArgumentException>(() => options.UseFailover(Array.Empty<Uri>()));
    }

    [Test]
    public void UseFailover_WithANullEndpoint_Throws()
    {
        TcgDexOptions options = new();

        Should.Throw<ArgumentException>(
            () => options.UseFailover([new Uri("https://one.example/v2/"), null!]));
    }

    [Test]
    public void UseFailover_WithARelativeEndpoint_Throws()
    {
        TcgDexOptions options = new();

        Should.Throw<ArgumentException>(
            () => options.UseFailover(new Uri("/v2/", UriKind.Relative)));
    }

    [Test]
    public void UseFailover_WithAnUndefinedMirror_Throws()
    {
        TcgDexOptions options = new();

        Should.Throw<ArgumentOutOfRangeException>(
            () => options.UseFailover((TcgDexMirror)999));
    }

    [TestCase(0)]
    [TestCase(-5)]
    public void Validate_RejectsANonPositiveAttemptTimeout(int seconds)
    {
        TcgDexOptions options = new() { FailoverAttemptTimeout = TimeSpan.FromSeconds(seconds) };

        Should.Throw<ArgumentException>(options.Validate);
    }

    [Test]
    public void Validate_AcceptsAnInfiniteAttemptTimeout()
    {
        // Infinite is a real choice, not an oversight: it leaves failover working
        // for refused connections and gateway errors, and gives up only the
        // ability to recover from a node that hangs.
        TcgDexOptions options = new() { FailoverAttemptTimeout = Timeout.InfiniteTimeSpan };

        Should.NotThrow(options.Validate);
    }

    [Test]
    public void Validate_RejectsANegativeCooldown()
    {
        TcgDexOptions options = new() { FailoverCooldown = TimeSpan.FromSeconds(-1) };

        Should.Throw<ArgumentException>(options.Validate);
    }

    [Test]
    public void Validate_AcceptsAZeroCooldown()
    {
        // Zero means "re-try a failed endpoint on every request", which is
        // meaningful — and distinct from negative, which is a mistake.
        TcgDexOptions options = new() { FailoverCooldown = TimeSpan.Zero };

        Should.NotThrow(options.Validate);
    }

    [Test]
    public void UseFailover_ProducesOptionsThatStillValidate()
    {
        TcgDexOptions options = new();

        options.UseFailover();

        Should.NotThrow(options.Validate);
    }
}
