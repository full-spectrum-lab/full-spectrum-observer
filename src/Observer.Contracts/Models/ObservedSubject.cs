namespace FullSpectrum.Observer.Contracts.Models;

/// <summary>
/// The observed subject. This is analysis context ONLY.
/// Red line #1: this entity MUST NOT carry any login / authentication / session / token fields.
/// It is never treated as a login identity anywhere in the Observer.
/// </summary>
public sealed record ObservedSubject
{
    /// <summary>Stable subject identifier, e.g. <c>SUBJ-2026-0007</c>.</summary>
    public required string LocalSubjectId { get; init; }

    /// <summary>Subject type, e.g. <c>AI_AGENT</c> / <c>DATASET</c> / <c>PIPELINE</c>.</summary>
    public required string SubjectType { get; init; }

    /// <summary>Business scenario mode, e.g. <c>CROSS_BORDER_PAYMENT</c>.</summary>
    public required string Mode { get; init; }

    /// <summary>Concentration tier (<c>TIER_1/2/3</c>); may be null.</summary>
    public string? ConcentrationTier { get; init; }

    /// <summary>Creation timestamp (ISO-8601 UTC). Immutable after creation.</summary>
    public required string CreatedAt { get; init; }

    /// <summary>
    /// Builds the subject declaration used as analysis context. The declaration is a
    /// projection of the subject's own context fields; the richer <c>declaration</c>
    /// object (display_name / boundary / owner_operator) lives in the active
    /// <see cref="SubjectVersion"/> payload and is read from there.
    /// </summary>
    public SubjectDeclaration ToDeclaration() => new(SubjectType, Mode, ConcentrationTier);
}

/// <summary>
/// Lightweight declaration of an observed subject. This is context metadata, not identity.
/// </summary>
/// <param name="SubjectType">Subject type.</param>
/// <param name="Mode">Business scenario mode.</param>
/// <param name="ConcentrationTier">Concentration tier; null if unset.</param>
public sealed record SubjectDeclaration(string SubjectType, string Mode, string? ConcentrationTier)
{
    /// <summary>Parses a declaration object from a <see cref="SubjectVersion"/> payload JSON.</summary>
    public static SubjectDeclaration FromPayload(string payloadJson)
    {
        using var document = System.Text.Json.JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        string subjectType = root.TryGetProperty("subject_type", out var st) ? st.GetString() ?? string.Empty : string.Empty;
        string mode = root.TryGetProperty("mode", out var m) ? m.GetString() ?? string.Empty : string.Empty;
        string? tier = root.TryGetProperty("concentration_tier", out var t) ? t.GetString() : null;
        return new SubjectDeclaration(subjectType, mode, tier);
    }
}
