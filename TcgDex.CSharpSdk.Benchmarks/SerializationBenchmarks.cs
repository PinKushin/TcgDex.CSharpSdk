namespace TcgDex.Benchmarks;

using System.Text.Json.Serialization.Metadata;
using TcgDex.Models;
using TcgDex.Serialization;

/// <summary>
/// Deserializing a recorded card response.
/// </summary>
/// <remarks>
/// <para>
/// The SDK uses a source-generated <see cref="TcgDexJsonContext"/> rather than
/// reflection, and the stated reason was that it is faster on every call rather
/// than only at warm-up. That was an argument, not a measurement. This compares
/// the two directly so the claim is either supported or corrected.
/// </para>
/// <para>
/// The comparison is set up to be fair rather than flattering: both paths use
/// the same naming policy, the same case-insensitivity, and the same custom
/// converters, and both are warmed by BenchmarkDotNet before measurement — so
/// this is steady-state cost, not first-call metadata building, which would
/// favour the source generator by construction.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class SerializationBenchmarks
{
    private string _cardJson = string.Empty;
    private JsonSerializerOptions _reflectionOptions = new();
    private JsonTypeInfo<Card>? _cardTypeInfo;

    [GlobalSetup]
    public void Setup()
    {
        _cardJson = File.ReadAllText(Path.Combine("Fixtures", "card-pokemon-full.json"));
        _cardTypeInfo = (JsonTypeInfo<Card>)TcgDexJsonContext.Default.Options.GetTypeInfo(typeof(Card));

        _reflectionOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
    }

    /// <summary>The path the SDK actually ships.</summary>
    [Benchmark(Baseline = true)]
    public Card? SourceGenerated()
    {
        var typeInfo = (JsonTypeInfo<Card>)TcgDexJsonContext.Default.Options.GetTypeInfo(typeof(Card));

        return JsonSerializer.Deserialize(_cardJson, typeInfo);
    }

    /// <summary>
    /// The same source-generated metadata, resolved once instead of per call.
    /// </summary>
    /// <remarks>
    /// Separates two costs the baseline conflates: the generated
    /// deserialization code itself, and the dictionary lookup plus cast in
    /// <c>Options.GetTypeInfo(typeof(Card))</c> that the SDK performs on every
    /// single request. If this is materially faster than the baseline, the
    /// lookup is worth hoisting in the SDK rather than being repeated.
    /// </remarks>
    [Benchmark]
    public Card? SourceGeneratedHoisted() => JsonSerializer.Deserialize(_cardJson, _cardTypeInfo!);

    /// <summary>What the SDK would cost with reflection-based metadata.</summary>
    [Benchmark]
    public Card? ReflectionBased() => JsonSerializer.Deserialize<Card>(_cardJson, _reflectionOptions);
}
