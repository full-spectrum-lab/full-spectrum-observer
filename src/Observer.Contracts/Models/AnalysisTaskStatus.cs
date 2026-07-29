namespace FullSpectrum.Observer.Contracts.Models;

/// <summary>
/// Canonical analysis task status values.
///
/// Frozen authority: <c>故障提交与恢复状态机.md</c> (P0-05) + 实现授权基线 §P1-2.
/// The Job status enum is UPPER_SNAKE and EXACTLY the set below; it does NOT contain
/// <c>REVIEWED</c> —人工复核 is the independent <c>review_status</c> field
/// (NOT_REQUIRED / PENDING / REVIEWED), per CR-OBS-003-JOBSTATUS-001.
///
/// Transition discipline is enforced by <see cref="JobLifecycle"/>, not here.
///
/// Legacy pre-states kept for backward compatibility with the v0.3 WIP
/// (<see cref="AnalysisTask.Create"/> writes <c>Draft</c>; the WIP orchestrator
/// transiently writes <c>Running</c> as a UI-derivative). Neither is part of the
/// P0-05 committed chain; <c>Running</c> in particular MUST NOT be persisted as a
/// job status once the orchestrator is re-aligned (see ADR-OBS-V030-UI-001 原则⑥/⑦).
/// </summary>
public static class AnalysisTaskStatus
{
    // Legacy creation pre-state (not in the P0-05 committed chain).
    public const string Draft = "Draft";

    // Legacy UI-derivative; do not persist as a job status once the orchestrator
    // is re-aligned to the P0-05 machine.
    [System.Obsolete("UI-derived 'in progress' marker; not a P0-05 job status. Persist PRECHECK_PASSED..AUDIT_COMMITTED instead.")]
    public const string Running = "Running";

    // P0-05 ordered commit chain + explicit failure states (UPPER_SNAKE, frozen).
    public const string PreflightFailed = "PREFLIGHT_FAILED";
    public const string PrecheckPassed = "PRECHECK_PASSED";
    public const string SnapshotCommitted = "SNAPSHOT_COMMITTED";
    public const string EngineCompleted = "ENGINE_COMPLETED";
    public const string OutputValidated = "OUTPUT_VALIDATED";
    public const string ArtifactCommitted = "ARTIFACT_COMMITTED";
    public const string ObservationCommitted = "OBSERVATION_COMMITTED";
    public const string AuditCommitted = "AUDIT_COMMITTED";
    public const string Completed = "COMPLETED";
    public const string EngineFailed = "ENGINE_FAILED";
    public const string OutputValidationFailed = "OUTPUT_VALIDATION_FAILED";
    public const string ArtifactCommitFailed = "ARTIFACT_COMMIT_FAILED";
    public const string ObservationCommitFailed = "OBSERVATION_COMMIT_FAILED";
    public const string AuditCommitFailed = "AUDIT_COMMIT_FAILED";
    public const string CancelledBeforeEngine = "CANCELLED_BEFORE_ENGINE";
    public const string CancelRequestedEngineFinished = "CANCEL_REQUESTED_ENGINE_FINISHED";
    public const string RecoveryRequired = "RECOVERY_REQUIRED";
}
