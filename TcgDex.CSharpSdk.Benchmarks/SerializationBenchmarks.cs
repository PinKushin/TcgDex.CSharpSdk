using System;
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
    private string _withoutPricing = string.Empty;
    private string _withoutAttacks = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        _cardJson = File.ReadAllText(Path.Combine("Fixtures", "card-pokemon-full.json"));

        // Same card with the pricing block removed, so the difference isolates
        // TcgPlayerPricingConverter rather than payload size in general.
        _withoutPricing = StripProperty(_cardJson, "pricing");

        // A card that carries no attacks, which is where FlexibleStringConverter
        // does its work on the polymorphic damage field.
        _withoutAttacks = File.ReadAllText(Path.Combine("Fixtures", "card-energy.json"));
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

    /// <summary>
    /// Removes a top-level property from a JSON object, for isolating the cost
    /// of the converter that reads it.
    /// </summary>
    private static string StripProperty(string json, string name)
    {
        using var document = JsonDocument.Parse(json);
        using var buffer = new MemoryStream();
        using var writer = new Utf8JsonWriter(buffer);

        writer.WriteStartObject();

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.Ordinal))
            {
                property.WriteTo(writer);
            }
        }

        writer.WriteEndObject();
        writer.Flush();

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>The same card without its pricing block.</summary>
    [Benchmark]
    public Card? WithoutPricing()
        => JsonSerializer.Deserialize(_withoutPricing, _cardTypeInfo!);

    /// <summary>A card with no attacks, so no polymorphic damage to normalise.</summary>
    [Benchmark]
    public Card? WithoutAttacks()
        => JsonSerializer.Deserialize(_withoutAttacks, _cardTypeInfo!);
    /// <summary>
    /// The same model through Newtonsoft.Json, as a control.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not a candidate — Newtonsoft is not trim- or AOT-safe, so it could not
    /// be adopted here whatever it measures. It is a **diagnostic**: it is a
    /// fully independent implementation rather than a wrapper over
    /// System.Text.Json, so if it lands near the same number then the cost is
    /// this SDK's model shape rather than its serializer.
    /// </para>
    /// <para>
    /// It has no equivalent of the two custom converters, and does not need
    /// them: Newtonsoft coerces a JSON number into a string property on its
    /// own, and maps the dynamic pricing keys through its dictionary handling.
    /// That leniency is exactly what System.Text.Json declines to do, which is
    /// why this SDK writes the converters — so this row does slightly less
    /// work, and is a floor rather than a like-for-like.
    /// </para>
    /// </remarks>
    [Benchmark]
    public Card? NewtonsoftBased()
        => Newtonsoft.Json.JsonConvert.DeserializeObject<Card>(_cardJson);}
