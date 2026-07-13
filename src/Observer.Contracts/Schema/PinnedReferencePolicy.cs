using System.Text.Json;
using FullSpectrum.Observer.Contracts.ReasonCodes;

namespace FullSpectrum.Observer.Contracts.Schema;

public static class PinnedReferencePolicy
{
    private static readonly string[] SnapshotReferenceNames =
    [
        "engine", "adapter", "scenario", "profile", "knowledge", "report_template", "schema_set",
    ];

    private static readonly HashSet<string> FloatingTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "latest", "current", "head", "main", "master", "stable", "*",
    };

    public static IReadOnlyList<SchemaValidationIssue> ValidateRuntimeSnapshot(JsonElement snapshot)
    {
        var issues = new List<SchemaValidationIssue>();
        foreach (string name in SnapshotReferenceNames)
        {
            if (!snapshot.TryGetProperty(name, out JsonElement reference) || reference.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new SchemaValidationIssue(FoundationReasonCodes.SNAPSHOT_REFERENCE_MISSING, $"$.{name}", "Pinned reference is missing."));
                continue;
            }

            ValidateText(reference, name, "id", issues);
            ValidateVersion(reference, name, issues);
            ValidateSha256(reference, name, issues);
        }
        return issues;
    }

    private static void ValidateText(JsonElement reference, string referenceName, string property, List<SchemaValidationIssue> issues)
    {
        if (!reference.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            issues.Add(new SchemaValidationIssue(FoundationReasonCodes.SNAPSHOT_REFERENCE_MISSING, $"$.{referenceName}.{property}", "Reference field is missing."));
        }
    }

    private static void ValidateVersion(JsonElement reference, string referenceName, List<SchemaValidationIssue> issues)
    {
        if (!reference.TryGetProperty("version", out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            issues.Add(new SchemaValidationIssue(FoundationReasonCodes.SNAPSHOT_REFERENCE_MISSING, $"$.{referenceName}.version", "Exact version is missing."));
            return;
        }
        string version = value.GetString() ?? string.Empty;
        bool floating = FloatingTokens.Contains(version)
            || version.Contains('*')
            || version.StartsWith('[')
            || version.StartsWith('(')
            || version.Contains(">=", StringComparison.Ordinal)
            || version.Contains("<=", StringComparison.Ordinal);
        if (floating)
        {
            issues.Add(new SchemaValidationIssue(FoundationReasonCodes.SNAPSHOT_FLOATING_VERSION_FORBIDDEN, $"$.{referenceName}.version", "Floating version is forbidden."));
        }
    }

    private static void ValidateSha256(JsonElement reference, string referenceName, List<SchemaValidationIssue> issues)
    {
        if (!reference.TryGetProperty("sha256", out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            issues.Add(new SchemaValidationIssue(FoundationReasonCodes.SNAPSHOT_REFERENCE_MISSING, $"$.{referenceName}.sha256", "Artifact SHA-256 is missing."));
            return;
        }
        string digest = value.GetString() ?? string.Empty;
        bool valid = digest.Length == 64 && digest.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
        bool zero = digest.All(static character => character == '0');
        if (!valid || zero)
        {
            issues.Add(new SchemaValidationIssue(FoundationReasonCodes.SNAPSHOT_DIGEST_MISMATCH, $"$.{referenceName}.sha256", "Artifact SHA-256 is invalid or is a zero placeholder."));
        }
    }
}
