using FluentAssertions;
using FullSpectrum.Observer.Contracts.Models;
using Xunit;

namespace FullSpectrum.Observer.Tests.Unit;

/// <summary>
/// M2 P1 main line — forward-only lifecycle guard (<see cref="JobLifecycle.CanAdvance"/>) and the
/// UI "进行中" derivation (<see cref="JobLifecycle.IsInProgress"/>). These encode the
/// "状态机只前进" discipline for the Web analysis orchestration's actual persisted order
/// (Draft → PRECHECK_PASSED → ENGINE_COMPLETED → OUTPUT_VALIDATED → SNAPSHOT_COMMITTED →
/// ARTIFACT_COMMITTED → OBSERVATION_COMMITTED → AUDIT_COMMITTED → COMPLETED) plus the failure /
/// recovery branches.
/// </summary>
public sealed class JobLifecycleAdvanceTests
{
    [Fact]
    public void CanAdvance_allows_strict_forward_progress_along_the_commit_chain()
    {
        JobLifecycle.CanAdvance(AnalysisTaskStatus.Draft, AnalysisTaskStatus.PrecheckPassed).Should().BeTrue();
        JobLifecycle.CanAdvance(AnalysisTaskStatus.PrecheckPassed, AnalysisTaskStatus.EngineCompleted).Should().BeTrue();
        JobLifecycle.CanAdvance(AnalysisTaskStatus.EngineCompleted, AnalysisTaskStatus.OutputValidated).Should().BeTrue();
        JobLifecycle.CanAdvance(AnalysisTaskStatus.OutputValidated, AnalysisTaskStatus.SnapshotCommitted).Should().BeTrue();
        JobLifecycle.CanAdvance(AnalysisTaskStatus.SnapshotCommitted, AnalysisTaskStatus.ArtifactCommitted).Should().BeTrue();
        JobLifecycle.CanAdvance(AnalysisTaskStatus.ArtifactCommitted, AnalysisTaskStatus.ObservationCommitted).Should().BeTrue();
        JobLifecycle.CanAdvance(AnalysisTaskStatus.ObservationCommitted, AnalysisTaskStatus.AuditCommitted).Should().BeTrue();
        JobLifecycle.CanAdvance(AnalysisTaskStatus.AuditCommitted, AnalysisTaskStatus.Completed).Should().BeTrue();
    }

    [Fact]
    public void CanAdvance_allows_skipping_ahead_without_regressing()
    {
        // Forward jumps are allowed; only regressions are forbidden.
        JobLifecycle.CanAdvance(AnalysisTaskStatus.PrecheckPassed, AnalysisTaskStatus.Completed).Should().BeTrue();
        JobLifecycle.CanAdvance(AnalysisTaskStatus.EngineCompleted, AnalysisTaskStatus.AuditCommitted).Should().BeTrue();
    }

    [Fact]
    public void CanAdvance_self_transition_is_idempotent_and_allowed()
    {
        JobLifecycle.CanAdvance(AnalysisTaskStatus.EngineCompleted, AnalysisTaskStatus.EngineCompleted).Should().BeTrue();
        JobLifecycle.CanAdvance(AnalysisTaskStatus.Completed, AnalysisTaskStatus.Completed).Should().BeTrue();
    }

    [Fact]
    public void CanAdvance_rejects_any_backward_transition()
    {
        JobLifecycle.CanAdvance(AnalysisTaskStatus.Completed, AnalysisTaskStatus.Draft).Should().BeFalse();
        JobLifecycle.CanAdvance(AnalysisTaskStatus.Completed, AnalysisTaskStatus.AuditCommitted).Should().BeFalse();
        JobLifecycle.CanAdvance(AnalysisTaskStatus.EngineCompleted, AnalysisTaskStatus.PrecheckPassed).Should().BeFalse();
        JobLifecycle.CanAdvance(AnalysisTaskStatus.AuditCommitted, AnalysisTaskStatus.ArtifactCommitted).Should().BeFalse();
    }

    [Fact]
    public void CanAdvance_allows_failure_branch_from_any_in_progress_state()
    {
        JobLifecycle.CanAdvance(AnalysisTaskStatus.PrecheckPassed, AnalysisTaskStatus.EngineFailed).Should().BeTrue();
        JobLifecycle.CanAdvance(AnalysisTaskStatus.EngineCompleted, AnalysisTaskStatus.OutputValidationFailed).Should().BeTrue();
        JobLifecycle.CanAdvance(AnalysisTaskStatus.ArtifactCommitted, AnalysisTaskStatus.ArtifactCommitFailed).Should().BeTrue();
        JobLifecycle.CanAdvance(AnalysisTaskStatus.ObservationCommitted, AnalysisTaskStatus.ObservationCommitFailed).Should().BeTrue();
    }

    [Fact]
    public void CanAdvance_allows_recovery_branch_and_re_entry_at_snapshot_committed()
    {
        JobLifecycle.CanAdvance(AnalysisTaskStatus.EngineCompleted, AnalysisTaskStatus.RecoveryRequired).Should().BeTrue();
        JobLifecycle.CanAdvance(AnalysisTaskStatus.AuditCommitFailed, AnalysisTaskStatus.RecoveryRequired).Should().BeTrue();
        JobLifecycle.CanAdvance(AnalysisTaskStatus.RecoveryRequired, AnalysisTaskStatus.SnapshotCommitted).Should().BeTrue();
    }

    [Fact]
    public void CanAdvance_forbids_recovery_skipping_straight_to_completed()
    {
        JobLifecycle.CanAdvance(AnalysisTaskStatus.RecoveryRequired, AnalysisTaskStatus.Completed).Should().BeFalse();
        JobLifecycle.CanAdvance(AnalysisTaskStatus.EngineFailed, AnalysisTaskStatus.Completed).Should().BeFalse();
    }

    [Fact]
    public void IsInProgress_derives_from_canonical_states_and_excludes_completed_and_failures()
    {
        // In-progress: [PRECHECK_PASSED … AUDIT_COMMITTED].
        JobLifecycle.IsInProgress(AnalysisTaskStatus.PrecheckPassed).Should().BeTrue();
        JobLifecycle.IsInProgress(AnalysisTaskStatus.EngineCompleted).Should().BeTrue();
        JobLifecycle.IsInProgress(AnalysisTaskStatus.SnapshotCommitted).Should().BeTrue();
        JobLifecycle.IsInProgress(AnalysisTaskStatus.OutputValidated).Should().BeTrue();
        JobLifecycle.IsInProgress(AnalysisTaskStatus.ArtifactCommitted).Should().BeTrue();
        JobLifecycle.IsInProgress(AnalysisTaskStatus.ObservationCommitted).Should().BeTrue();
        JobLifecycle.IsInProgress(AnalysisTaskStatus.AuditCommitted).Should().BeTrue();

        // Not in-progress: legacy Draft, the single completed state, recovery, and all failure states.
        JobLifecycle.IsInProgress(AnalysisTaskStatus.Draft).Should().BeFalse();
        JobLifecycle.IsInProgress(AnalysisTaskStatus.Completed).Should().BeFalse();
        JobLifecycle.IsInProgress(AnalysisTaskStatus.RecoveryRequired).Should().BeFalse();
        JobLifecycle.IsInProgress(AnalysisTaskStatus.PreflightFailed).Should().BeFalse();
        JobLifecycle.IsInProgress(AnalysisTaskStatus.EngineFailed).Should().BeFalse();
        JobLifecycle.IsInProgress(AnalysisTaskStatus.AuditCommitFailed).Should().BeFalse();
    }
}
