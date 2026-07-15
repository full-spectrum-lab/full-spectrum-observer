using System.Collections.Immutable;

namespace FullSpectrum.Observer.Contracts.Models;

/// <summary>
/// A conflict observation surfaced by the Engine. Stored verbatim. The product layer may add
/// a review flag / note (human_review_required is Engine-owned and never overwritten by Observer).
/// Observer does not attribute blame to a single lower-level subject (red line #9).
/// </summary>
public sealed record ConflictObservation
{
    /// <summary>Observation identifier, e.g. <c>OBS-2026-0033-01</c>.</summary>
    public required string ObservationId { get; init; }

    /// <summary>Owning result identifier (FK to <c>analysis_results.result_id</c>).</summary>
    public required string ResultId { get; init; }

    /// <summary>Conflict type (Engine-owned, pass-through).</summary>
    public required string ConflictType { get; init; }

    /// <summary>Involved subjects (Engine-owned, pass-through).</summary>
    public required ImmutableArray<string> InvolvedSubjects { get; init; }

    /// <summary>Severity (Engine-owned, pass-through).</summary>
    public required string Severity { get; init; }

    /// <summary>Whether human review is required (Engine-owned, pass-through).</summary>
    public required bool HumanReviewRequired { get; init; }

    /// <summary>Reason code; nullable.</summary>
    public string? ReasonCode { get; init; }

    /// <summary>Missing context array; nullable.</summary>
    public ImmutableArray<string>? MissingContext { get; init; }

    /// <summary>Product-layer review flag (<c>PENDING</c> / <c>DONE</c>); null until reviewed.</summary>
    public string? ReviewFlag { get; init; }

    /// <summary>Product-layer review note; null until reviewed. Never written back to Engine.</summary>
    public string? ReviewNote { get; init; }
}
