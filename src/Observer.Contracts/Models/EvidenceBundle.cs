using System.Collections.Immutable;

namespace FullSpectrum.Observer.Contracts.Models;

/// <summary>
/// An evidence bundle for a result. The evidence digest is the Engine-computed fingerprint
/// and is stored verbatim (red line #8). References are CER/RV file references, stored as-is.
/// </summary>
public sealed record EvidenceBundle
{
    /// <summary>Bundle identifier, e.g. <c>EVID-2026-0033</c>.</summary>
    public required string BundleId { get; init; }

    /// <summary>Owning result identifier (FK to <c>analysis_results.result_id</c>).</summary>
    public required string ResultId { get; init; }

    /// <summary>Evidence digest (Engine-computed fingerprint). Verbatim.</summary>
    public required string EvidenceDigest { get; init; }

    /// <summary>Evidence reference list (CER/RV file refs). Verbatim.</summary>
    public required ImmutableArray<string> References { get; init; }
}
