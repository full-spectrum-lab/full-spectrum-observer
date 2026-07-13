using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;

namespace FullSpectrum.Observer.Contracts.Canonicalization;

public static class FsObsCanonicalizer
{
    public const string SerializationId = "FS-OBS-CANON-1";

    public static byte[] Canonicalize(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(utf8Json.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });
            return Canonicalize(document.RootElement);
        }
        catch (CanonicalizationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or FormatException or OverflowException)
        {
            throw new CanonicalizationException("FS-OBS-CANON-1 canonicalization failed.", exception);
        }
    }

    public static byte[] Canonicalize(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = false,
        }))
        {
            WriteElement(writer, element);
        }
        return stream.ToArray();
    }

    public static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void WriteElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject().OrderBy(static item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteElement(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteElement(writer, item);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(CanonicalizeNumber(element.GetRawText()), skipInputValidation: false);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidDataException($"Unsupported JSON value kind: {element.ValueKind}.");
        }
    }

    private static string CanonicalizeNumber(string raw)
    {
        if (raw.IndexOfAny(['.', 'e', 'E']) < 0)
        {
            if (!BigInteger.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out BigInteger integer))
            {
                throw new InvalidDataException($"Invalid JSON integer: {raw}.");
            }
            return integer.ToString(CultureInfo.InvariantCulture);
        }

        if (!decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal value))
        {
            throw new InvalidDataException($"FS-OBS-CANON-1 only accepts finite decimal JSON numbers: {raw}.");
        }

        string normalized = value.ToString("G29", CultureInfo.InvariantCulture);
        return normalized.Contains('E')
            ? normalized.Replace("E+", "e", StringComparison.Ordinal).Replace("E", "e", StringComparison.Ordinal)
            : normalized;
    }
}
