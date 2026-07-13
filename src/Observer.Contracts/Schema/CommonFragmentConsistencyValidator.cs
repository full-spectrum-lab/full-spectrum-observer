using System.Text.Json;

namespace FullSpectrum.Observer.Contracts.Schema;

internal static class CommonFragmentConsistencyValidator
{
    private const string Sha256Pattern = "^[0-9a-f]{64}$";
    private const string UuidPattern = "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$";
    private const string VersionPattern = "^[A-Za-z0-9][A-Za-z0-9._+-]{0,63}$";

    public static void Validate(JsonElement schema)
    {
        Walk(schema, "$schema");
    }

    private static void Walk(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            string? pattern = element.TryGetProperty("pattern", out JsonElement patternElement)
                ? patternElement.GetString()
                : null;
            if (pattern is Sha256Pattern or UuidPattern or VersionPattern)
            {
                if (!element.TryGetProperty("type", out JsonElement type) || type.GetString() != "string")
                {
                    throw new InvalidDataException($"Common fragment pattern must have string type at {path}.");
                }
            }

            foreach (JsonProperty property in element.EnumerateObject())
            {
                Walk(property.Value, path + "." + property.Name);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement item in element.EnumerateArray())
            {
                Walk(item, $"{path}[{index}]");
                index++;
            }
        }
    }
}
