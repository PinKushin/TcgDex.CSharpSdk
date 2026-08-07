namespace TcgDex.Benchmarks;

using TcgDex.Querying;

/// <summary>
/// Translating an expression tree into a REST query string.
/// </summary>
/// <remarks>
/// <para>
/// The design decision under measurement: the builder <b>walks</b> expression
/// trees and never calls <c>Expression.Compile()</c>. That was chosen for AOT
/// safety, with the secondary claim that it is also faster because no runtime
/// codegen happens at all. The first half is proven by the AOT smoke test; this
/// is the second half.
/// </para>
/// <para>
/// Read these as a regression baseline rather than a headline. A query is built
/// once per request against a network round trip of tens of milliseconds, so
/// even a bad number here would be swamped — the reason to measure is to notice
/// if it ever moves by an order of magnitude, which would mean something started
/// allocating or compiling.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class QueryBenchmarks
{
    private const int MinimumHp = 100;

    [Benchmark(Baseline = true)]
    public string SingleEqualityFilter()
        => new CardQuery().Where(c => c.Name == "Furret").ToQueryString();

    [Benchmark]
    public string ComparisonFilter()
        => new CardQuery().Where(c => c.Hp > MinimumHp).ToQueryString();

    /// <summary>A captured local, which is the case that would tempt a compile.</summary>
    [Benchmark]
    public string CapturedVariable()
    {
        var minimum = MinimumHp;

        return new CardQuery().Where(c => c.Hp > minimum).ToQueryString();
    }

    /// <summary>Closer to what a real search screen builds.</summary>
    [Benchmark]
    public string MultipleFiltersWithSortAndPaging()
        => new CardQuery()
            .Where(c => c.Name.Contains("Pikachu"))
            .Where(c => c.Hp > MinimumHp)
            .Where(c => c.Rarity == "Rare")
            .OrderByDescending(c => c.Name)
            .Page(2, 50)
            .ToQueryString();
}
