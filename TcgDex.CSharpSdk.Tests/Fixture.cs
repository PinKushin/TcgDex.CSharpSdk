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
        var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", fileName);

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
    {
        var typeInfo = (JsonTypeInfo<T>)TcgDexJsonContext.Default.Options.GetTypeInfo(typeof(T));
        var result = JsonSerializer.Deserialize(ReadText(fileName), typeInfo);

        return result ?? throw new InvalidOperationException(
            $"Fixture '{fileName}' deserialized to null, which means the recorded " +
            "response no longer matches the model it is loaded into.");
    }
}
