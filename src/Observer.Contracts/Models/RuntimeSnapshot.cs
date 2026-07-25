namespace FullSpectrum.Observer.Contracts.Models;

/// <summary>
/// Runtime snapshot for a result. The <see cref="EngineVersion"/> column is pinned to
/// <c>1.5.0</c> (DB CHECK) and <see cref="InputDigest"/> must equal
/// <c>analysis_tasks.content_digest</c> (replay anchor, red line #8).
/// </summary>
public sealed record RuntimeSnapshot
{
    /// <summary>Snapshot identifier, e.g. <c>SNP-2026-0033</c>.</summary>
    public required string SnapshotId { get; init; }

    /// <summary>Owning result identifier (FK to <c>analysis_results.result_id</c>).</summary>
    public required string ResultId { get; init; }

    /// <summary>Analyzer version (pinned).</summary>
    public required string AnalyzerVersion { get; init; }

    /// <summary>Engine version; pinned to <c>1.5.0</c> at DB level.</summary>
    public required string EngineVersion { get; init; }

    /// <summary>Profile version (pinned).</summary>
    public required string ProfileVersion { get; init; }

    /// <summary>Schema version (pinned).</summary>
    public required string SchemaVersion { get; init; }

    /// <summary>Input digest; must equal the task content_digest.</summary>
    public required string InputDigest { get; init; }

    /// <summary>Configuration digest.</summary>
    public required string ConfigDigest { get; init; }

    /// <summary>Runtime configuration digest (sha256 of Engine runtime config).</summary>
    public required string RuntimeDigest { get; init; }

    /// <summary>DET-001-FIX — the simulation_id resolved and sent to the Engine worker (audit trace).</summary>
    public string? ResolvedSimulationId { get; init; }
}
