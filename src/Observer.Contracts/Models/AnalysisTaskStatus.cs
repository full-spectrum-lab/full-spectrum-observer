namespace FullSpectrum.Observer.Contracts.Models;

/// <summary>
/// Canonical analysis task status values (R1-D ordered commit chain + explicit failure states).
/// These constants back the <c>analysis_tasks.status</c> CHECK constraint.
/// </summary>
public static class AnalysisTaskStatus
{
    public const string Draft = "Draft";
    public const string PreflightFailed = "PreflightFailed";
    public const string PrecheckPassed = "PRECHECK_PASSED";
    public const string SnapshotCommitted = "SNAPSHOT_COMMITTED";
    public const string Running = "Running";
    public const string EngineCompleted = "ENGINE_COMPLETED";
    public const string OutputValidated = "OUTPUT_VALIDATED";
    public const string ArtifactCommitted = "ARTIFACT_COMMITTED";
    public const string ObservationCommitted = "OBSERVATION_COMMITTED";
    public const string AuditCommitted = "AUDIT_COMMITTED";
    public const string Completed = "Completed";
    public const string EngineFailed = "ENGINE_FAILED";
    public const string OutputValidationFailed = "OUTPUT_VALIDATION_FAILED";
    public const string ArtifactCommitFailed = "ARTIFACT_COMMIT_FAILED";
    public const string ObservationCommitFailed = "OBSERVATION_COMMIT_FAILED";
    public const string AuditCommitFailed = "AUDIT_COMMIT_FAILED";
    public const string CancelledBeforeEngine = "CANCELLED_BEFORE_ENGINE";
    public const string CancelRequestedEngineFinished = "CANCEL_REQUESTED_ENGINE_FINISHED";
    public const string RecoveryRequired = "RECOVERY_REQUIRED";
    public const string Reviewed = "Reviewed";
}
