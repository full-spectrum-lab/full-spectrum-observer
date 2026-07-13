using System.Text.Json;
using System.Text.Json.Serialization;

namespace FullSpectrum.Observer.Contracts.Serialization;

public static class FoundationJson
{
    public static JsonSerializerOptions CreateOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            NumberHandling = JsonNumberHandling.Strict,
            WriteIndented = false,
            MaxDepth = 64,
        };
    }

    public static T Deserialize<T>(ReadOnlySpan<byte> utf8Json)
    {
        T? value = JsonSerializer.Deserialize<T>(utf8Json, CreateOptions());
        if (value is null)
        {
            throw new JsonException($"Deserialization returned null for {typeof(T).Name}.");
        }
        return value;
    }

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, CreateOptions());
}
