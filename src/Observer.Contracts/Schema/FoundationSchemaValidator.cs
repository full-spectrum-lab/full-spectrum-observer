using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FullSpectrum.Observer.Contracts.ReasonCodes;

namespace FullSpectrum.Observer.Contracts.Schema;

public sealed class FoundationSchemaValidator
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    public SchemaValidationReport Validate(JsonElement schema, JsonElement instance)
    {
        string schemaId = schema.TryGetProperty("$id", out JsonElement id) ? id.GetString() ?? "unknown" : "unknown";
        var issues = new List<SchemaValidationIssue>();
        ValidateNode(schema, instance, "$", issues);
        return new SchemaValidationReport(schemaId, issues);
    }

    private static void ValidateNode(JsonElement schema, JsonElement instance, string path, List<SchemaValidationIssue> issues)
    {
        if (schema.TryGetProperty("oneOf", out JsonElement oneOf))
        {
            int matches = 0;
            foreach (JsonElement branch in oneOf.EnumerateArray())
            {
                var branchIssues = new List<SchemaValidationIssue>();
                ValidateNode(branch, instance, path, branchIssues);
                if (branchIssues.Count == 0)
                {
                    matches++;
                }
            }
            if (matches != 1)
            {
                issues.Add(new SchemaValidationIssue(FoundationReasonCodes.SCHEMA_VALUE_OUT_OF_RANGE, path, $"oneOf expected exactly one match, actual {matches}."));
                return;
            }
        }

        if (schema.TryGetProperty("allOf", out JsonElement allOf))
        {
            foreach (JsonElement part in allOf.EnumerateArray())
            {
                if (part.TryGetProperty("if", out JsonElement condition))
                {
                    var conditionIssues = new List<SchemaValidationIssue>();
                    ValidateNode(condition, instance, path, conditionIssues);
                    if (conditionIssues.Count == 0 && part.TryGetProperty("then", out JsonElement thenSchema))
                    {
                        ValidateNode(thenSchema, instance, path, issues);
                    }
                }
                else
                {
                    ValidateNode(part, instance, path, issues);
                }
            }
        }

        if (schema.TryGetProperty("type", out JsonElement typeElement))
        {
            if (!MatchesType(typeElement, instance))
            {
                issues.Add(new SchemaValidationIssue(FoundationReasonCodes.SCHEMA_TYPE_INVALID, path, $"Expected type {typeElement.GetRawText()}, actual {instance.ValueKind}."));
                return;
            }
        }

        if (schema.TryGetProperty("const", out JsonElement constant) && !JsonValueEquals(constant, instance))
        {
            issues.Add(new SchemaValidationIssue(FoundationReasonCodes.SCHEMA_VALUE_OUT_OF_RANGE, path, "Value does not match const."));
        }

        if (schema.TryGetProperty("enum", out JsonElement enumValues))
        {
            bool found = enumValues.EnumerateArray().Any(candidate => JsonValueEquals(candidate, instance));
            if (!found)
            {
                issues.Add(new SchemaValidationIssue(FoundationReasonCodes.SCHEMA_VALUE_OUT_OF_RANGE, path, "Value is outside the enum."));
            }
        }

        switch (instance.ValueKind)
        {
            case JsonValueKind.Object:
                ValidateObject(schema, instance, path, issues);
                break;
            case JsonValueKind.Array:
                ValidateArray(schema, instance, path, issues);
                break;
            case JsonValueKind.String:
                ValidateString(schema, instance.GetString() ?? string.Empty, path, issues);
                break;
            case JsonValueKind.Number:
                ValidateNumber(schema, instance, path, issues);
                break;
        }
    }

    private static void ValidateObject(JsonElement schema, JsonElement instance, string path, List<SchemaValidationIssue> issues)
    {
        var knownProperties = new HashSet<string>(StringComparer.Ordinal);
        if (schema.TryGetProperty("properties", out JsonElement properties))
        {
            foreach (JsonProperty property in properties.EnumerateObject())
            {
                knownProperties.Add(property.Name);
                if (instance.TryGetProperty(property.Name, out JsonElement value))
                {
                    ValidateNode(property.Value, value, Append(path, property.Name), issues);
                }
            }
        }

        if (schema.TryGetProperty("required", out JsonElement required))
        {
            foreach (JsonElement item in required.EnumerateArray())
            {
                string propertyName = item.GetString() ?? string.Empty;
                if (!instance.TryGetProperty(propertyName, out _))
                {
                    issues.Add(new SchemaValidationIssue(FoundationReasonCodes.SCHEMA_REQUIRED_MISSING, Append(path, propertyName), "Required property is missing."));
                }
            }
        }

        if (schema.TryGetProperty("additionalProperties", out JsonElement additional) && additional.ValueKind == JsonValueKind.False)
        {
            foreach (JsonProperty property in instance.EnumerateObject())
            {
                if (!knownProperties.Contains(property.Name))
                {
                    issues.Add(new SchemaValidationIssue(FoundationReasonCodes.SCHEMA_ADDITIONAL_PROPERTY, Append(path, property.Name), "Additional property is forbidden."));
                }
            }
        }
    }

    private static void ValidateArray(JsonElement schema, JsonElement instance, string path, List<SchemaValidationIssue> issues)
    {
        int length = instance.GetArrayLength();
        if (schema.TryGetProperty("minItems", out JsonElement minItems) && length < minItems.GetInt32())
        {
            issues.Add(new SchemaValidationIssue(FoundationReasonCodes.SCHEMA_VALUE_OUT_OF_RANGE, path, "Array contains fewer than minItems."));
        }
        if (schema.TryGetProperty("items", out JsonElement itemSchema))
        {
            int index = 0;
            foreach (JsonElement item in instance.EnumerateArray())
            {
                ValidateNode(itemSchema, item, $"{path}[{index}]", issues);
                index++;
            }
        }
    }

    private static void ValidateString(JsonElement schema, string value, string path, List<SchemaValidationIssue> issues)
    {
        if (schema.TryGetProperty("minLength", out JsonElement minLength) && value.Length < minLength.GetInt32())
        {
            issues.Add(new SchemaValidationIssue(FoundationReasonCodes.SCHEMA_VALUE_OUT_OF_RANGE, path, "String is shorter than minLength."));
        }
        if (schema.TryGetProperty("pattern", out JsonElement pattern))
        {
            string expression = pattern.GetString() ?? string.Empty;
            if (!Regex.IsMatch(value, expression, RegexOptions.CultureInvariant, RegexTimeout))
            {
                issues.Add(new SchemaValidationIssue(FoundationReasonCodes.SCHEMA_VALUE_OUT_OF_RANGE, path, "String does not match pattern."));
            }
        }
    }

    private static void ValidateNumber(JsonElement schema, JsonElement instance, string path, List<SchemaValidationIssue> issues)
    {
        if (!decimal.TryParse(instance.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal value))
        {
            issues.Add(new SchemaValidationIssue(FoundationReasonCodes.SCHEMA_TYPE_INVALID, path, "Number cannot be represented as a finite decimal."));
            return;
        }
        if (schema.TryGetProperty("minimum", out JsonElement minimum) && value < minimum.GetDecimal())
        {
            issues.Add(new SchemaValidationIssue(FoundationReasonCodes.SCHEMA_VALUE_OUT_OF_RANGE, path, "Number is below minimum."));
        }
        if (schema.TryGetProperty("maximum", out JsonElement maximum) && value > maximum.GetDecimal())
        {
            issues.Add(new SchemaValidationIssue(FoundationReasonCodes.SCHEMA_VALUE_OUT_OF_RANGE, path, "Number is above maximum."));
        }
    }

    private static bool MatchesType(JsonElement typeElement, JsonElement instance)
    {
        if (typeElement.ValueKind == JsonValueKind.Array)
        {
            return typeElement.EnumerateArray().Any(item => MatchesTypeName(item.GetString(), instance));
        }
        return MatchesTypeName(typeElement.GetString(), instance);
    }

    private static bool MatchesTypeName(string? type, JsonElement instance) => type switch
    {
        "object" => instance.ValueKind == JsonValueKind.Object,
        "array" => instance.ValueKind == JsonValueKind.Array,
        "string" => instance.ValueKind == JsonValueKind.String,
        "integer" => IsInteger(instance),
        "number" => instance.ValueKind == JsonValueKind.Number,
        "boolean" => instance.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => instance.ValueKind == JsonValueKind.Null,
        _ => true,
    };

    private static bool IsInteger(JsonElement instance)
    {
        if (instance.ValueKind != JsonValueKind.Number)
        {
            return false;
        }
        return decimal.TryParse(instance.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal value)
            && decimal.Truncate(value) == value;
    }

    private static bool JsonValueEquals(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
        {
            return false;
        }
        return left.ValueKind switch
        {
            JsonValueKind.String => string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal),
            JsonValueKind.Number => string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal),
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            _ => string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal),
        };
    }

    private static string Append(string path, string property) => path == "$" ? $"$.{property}" : $"{path}.{property}";
}
