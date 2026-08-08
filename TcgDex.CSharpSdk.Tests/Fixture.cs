namespace TcgDex.Tests;

using System.IO;
using System.Text.Json.Serialization.Metadata;
using TcgDex.Serialization;

/// <summary>
/// Loads the recorded live-API responses in <c>Fixtures/</c> and deserializes
/// them through the SDK's own serializer context, so these tests exercise the
/// exact configuration the SDK ships rather than a test-local one.
/// </summary>
internal static class Fixture
{
    internal static string ReadText(string fileName)
    {
        string path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", fileName);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Fixture '{fileName}' is missing. Recorded API responses live in " +
                "TcgDex.CSharpSdk.Tests/Fixtures and are copied to the test output.",
                path);
        }

        return File.ReadAllText(path);
    }

    internal static T Load<T>(string fileName)
        where T : notnull
        => Parse<T>(ReadText(fileName), fileName);

    /// <summary>
    /// Deserializes JSON through the SDK's own context, for the cases that need
    /// a recorded response altered — a field retyped to a shape the API's
    /// published contract allows but its current data does not happen to contain.
    /// </summary>
    internal static T Parse<T>(string json, string description = "the supplied JSON")
        where T : notnull
    {
        JsonTypeInfo<T> typeInfo = (JsonTypeInfo<T>)TcgDexJsonContext.Default.Options.GetTypeInfo(typeof(T));
        T? result = JsonSerializer.Deserialize(json, typeInfo);

        return result ?? throw new InvalidOperationException(
            $"Fixture '{description}' deserialized to null, which means the recorded " +
            "response no longer matches the model it is loaded into.");
    }
}
