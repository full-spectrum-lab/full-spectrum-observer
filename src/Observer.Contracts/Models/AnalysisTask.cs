using System.Collections.Immutable;

namespace FullSpectrum.Observer.Contracts.Models;

/// <summary>
/// An analysis task. The subject version and knowledge version set are locked at creation
/// time (foreign keys) and bind the evidence chain for replay. The lifecycle status follows
/// the R1-D ordered commit chain (see <see cref="AnalysisTaskStatus"/>).
/// </summary>
public sealed record AnalysisTask
{
    /// <summary>Task identifier, e.g. <c>TASK-2026-0033</c>.</summary>
    public required string TaskId { get; init; }

    /// <summary>Locked subject version identifier (FK to <c>subject_versions.version_id</c>).</summary>
    public required string SubjectVersionId { get; init; }

    /// <summary>Locked knowledge source version identifiers.</summary>
    public required ImmutableArray<string> KnowledgeVersionIds { get; init; }

    /// <summary>Raw input mode: <c>FORM</c> / <c>JSON_IMPORT</c> / <c>SANITIZED_FILE</c>.</summary>
    public required string InputMode { get; init; }

    /// <summary>Canonical input JSON (the normalized input).</summary>
    public required string CanonicalInput { get; init; }

    /// <summary>sha256 of <see cref="CanonicalInput"/>.</summary>
    public required string ContentDigest { get; init; }

    /// <summary>Desensitization trace JSON; null if none.</summary>
    public string? TransformTrace { get; init; }

    /// <summary>Retention mode: <c>SANITIZED_PERSISTENT</c> / <c>FULL_LOCAL</c> / <c>EPHEMERAL</c>.</summary>
    public required string RetentionMode { get; init; }

    /// <summary>Lifecycle status (R1-D ordered commit chain + failure states).</summary>
    public required string Status { get; init; }

    /// <summary>Independent human-review status (CR-OBS-003-JOBSTATUS-001). Does NOT alter the
    /// Engine execution fact and is never a Job status. Defaults to NOT_REQUIRED.</summary>
    public string ReviewStatus { get; init; } = JobLifecycle.ReviewStatus.NotRequired;

    /// <summary>Creation timestamp (ISO-8601 UTC).</summary>
    public required string CreatedAt { get; init; }

    /// <summary>Factory that creates a new Draft analysis task with a normalized input envelope.</summary>
    public static AnalysisTask Create(
        string taskId,
        string subjectVersionId,
        ImmutableArray<string> knowledgeVersionIds,
        RawAnalysisInput input,
        string retentionMode,
        string createdAtUtc) => new()
    {
        TaskId = taskId,
        SubjectVersionId = subjectVersionId,
        KnowledgeVersionIds = knowledgeVersionIds,
        InputMode = input.Mode,
        CanonicalInput = input.CanonicalInput,
        ContentDigest = input.ContentDigest,
        TransformTrace = input.TransformTrace,
        RetentionMode = retentionMode,
        Status = AnalysisTaskStatus.Draft,
        ReviewStatus = JobLifecycle.ReviewStatus.NotRequired,
        CreatedAt = createdAtUtc,
    };
}
