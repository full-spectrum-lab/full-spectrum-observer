namespace FullSpectrum.Observer.Contracts.Models;

/// <summary>
/// The analysis result for a task. The Engine's original conclusion is stored as-is
/// (pass-through; never recomputed, merged, or downgraded — red lines #6 / #9).
/// </summary>
public sealed record AnalysisResult
{
    /// <summary>Result identifier, e.g. <c>RES-2026-0033</c>.</summary>
    public required string ResultId { get; init; }

    /// <summary>Owning task identifier (FK to <c>analysis_tasks.task_id</c>).</summary>
    public required string TaskId { get; init; }

    /// <summary>Engine original conclusion JSON, stored verbatim.</summary>
    public required string ConclusionPayload { get; init; }

    /// <summary>Unknown state: <c>UNKNOWN</c> / <c>KNOWN</c> / <c>PARTIAL</c>. Displayed as-is.</summary>
    public required string UnknownState { get; init; }

    /// <summary>Hard gate flag. Displayed as-is; never downgraded to "no risk".</summary>
    public required bool HardGate { get; init; }

    /// <summary>Creation timestamp (ISO-8601 UTC).</summary>
    public required string CreatedAt { get; init; }
}
