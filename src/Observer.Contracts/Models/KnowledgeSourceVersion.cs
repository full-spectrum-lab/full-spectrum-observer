namespace FullSpectrum.Observer.Contracts.Models;

/// <summary>
/// A versioned snapshot of a <see cref="KnowledgeSource"/> content.
/// Carries a content digest (sha256) and an applicability scope. Once <c>Active</c> the row
/// is immutable (DB CHECK + application guard); the digest is never mutated.
/// </summary>
public sealed record KnowledgeSourceVersion
{
    /// <summary>Version identifier, e.g. <c>KSV-2026-0011-A1</c>.</summary>
    public required string VersionId { get; init; }

    /// <summary>Owning knowledge source identifier (FK to <c>knowledge_sources.source_id</c>).</summary>
    public required string SourceId { get; init; }

    /// <summary>Content sha256 fingerprint of the knowledge source.</summary>
    public required string Digest { get; init; }

    /// <summary>Applicability scope, e.g. <c>CROSS_BORDER_PAYMENT</c>.</summary>
    public required string Applicability { get; init; }

    /// <summary>Lifecycle status: <c>Draft</c> / <c>Active</c> / <c>Retired</c>.</summary>
    public required string Status { get; init; }

    /// <summary>Monotonic sequence within the same source (starts at 1).</summary>
    public required int Seq { get; init; }

    /// <summary>Payload JSON: knowledge source content / metadata.</summary>
    public required string Payload { get; init; }

    /// <summary>Version creation timestamp (ISO-8601 UTC).</summary>
    public required string CreatedAt { get; init; }

    /// <summary>Effective timestamp; populated when status becomes <c>Active</c>.</summary>
    public string? EffectiveTime { get; init; }

    /// <summary>Returns true when this version is the immutable Active version.</summary>
    public bool IsActive() => string.Equals(Status, "Active", System.StringComparison.Ordinal);

    /// <summary>Produces a new immutable Active version projection from this Draft.</summary>
    public KnowledgeSourceVersion Activate(string effectiveTimeUtc) => this with { Status = "Active", EffectiveTime = effectiveTimeUtc };

    /// <summary>Produces a Retired projection of this version.</summary>
    public KnowledgeSourceVersion Retire() => this with { Status = "Retired" };
}
