using FluentAssertions;
using FullSpectrum.Observer.Contracts.Models;
using Xunit;

namespace FullSpectrum.Observer.Tests.Unit;

/// <summary>
/// P0-05 commit &amp; recovery state-machine transition guards (实现授权基线 §P1-2 / 故障提交与恢复状态机.md).
/// Covers: legal ordered chain, illegal edges refused, terminal / recovery / failure classification,
/// the single "completed" gate, and the independent review_status values.
/// </summary>
public sealed class JobLifecycleGuardTests
{
    [Fact]
    public void Ordered_commit_chain_transitions_are_allowed()
    {
        JobLifecycle.CanTransition(AnalysisTaskStatus.PrecheckPassed, AnalysisTaskStatus.SnapshotCommitted).Should().BeTrue();
        JobLifecycle.CanTransition(AnalysisTaskStatus.SnapshotCommitted, AnalysisTaskStatus.EngineCompleted).Should().BeTrue();
        JobLifecycle.CanTransition(AnalysisTaskStatus.EngineCompleted, AnalysisTaskStatus.OutputValidated).Should().BeTrue();
        JobLifecycle.CanTransition(AnalysisTaskStatus.OutputValidated, AnalysisTaskStatus.ArtifactCommitted).Should().BeTrue();
        JobLifecycle.CanTransition(AnalysisTaskStatus.ArtifactCommitted, AnalysisTaskStatus.ObservationCommitted).Should().BeTrue();
        JobLifecycle.CanTransition(AnalysisTaskStatus.ObservationCommitted, AnalysisTaskStatus.AuditCommitted).Should().BeTrue();
        JobLifecycle.CanTransition(AnalysisTaskStatus.AuditCommitted, AnalysisTaskStatus.Completed).Should().BeTrue();
    }

    [Fact]
    public void Illegal_transition_is_refused_and_throws()
    {
        JobLifecycle.CanTransition(AnalysisTaskStatus.PrecheckPassed, AnalysisTaskStatus.Completed).Should().BeFalse();
        JobLifecycle.CanTransition(AnalysisTaskStatus.SnapshotCommitted, AnalysisTaskStatus.Completed).Should().BeFalse();
        // Engine output must never be jumped to completion without the commit chain.
        JobLifecycle.CanTransition(AnalysisTaskStatus.EngineCompleted, AnalysisTaskStatus.Completed).Should().BeFalse();

        var act = () => JobLifecycle.EnsureTransition(AnalysisTaskStatus.PrecheckPassed, AnalysisTaskStatus.Completed);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Illegal Job status transition*");
    }

    [Fact]
    public void Same_state_is_idempotent()
    {
        JobLifecycle.CanTransition(AnalysisTaskStatus.EngineCompleted, AnalysisTaskStatus.EngineCompleted).Should().BeTrue();
        JobLifecycle.Invoking(j => j.EnsureTransition(AnalysisTaskStatus.Completed, AnalysisTaskStatus.Completed))
            .Should().NotThrow();
    }

    [Fact]
    public void Completed_is_terminal_and_only_reachable_after_audit_committed()
    {
        JobLifecycle.IsTerminal(AnalysisTaskStatus.Completed).Should().BeTrue();
        JobLifecycle.CanTransition(AnalysisTaskStatus.Completed, AnalysisTaskStatus.SnapshotCommitted).Should().BeFalse();
        // Red line #7: COMPLETED only after AUDIT_COMMITTED.
        JobLifecycle.CanTransition(AnalysisTaskStatus.AuditCommitted, AnalysisTaskStatus.Completed).Should().BeTrue();
        JobLifecycle.CanTransition(AnalysisTaskStatus.ObservationCommitted, AnalysisTaskStatus.Completed).Should().BeFalse();
    }

    [Fact]
    public void Recovery_required_is_reachable_only_from_explicit_failures_and_re_enters_at_snapshot_committed()
    {
        JobLifecycle.IsRecoveryState(AnalysisTaskStatus.RecoveryRequired).Should().BeTrue();
        JobLifecycle.CanTransition(AnalysisTaskStatus.EngineFailed, AnalysisTaskStatus.RecoveryRequired).Should().BeTrue();
        JobLifecycle.CanTransition(AnalysisTaskStatus.ArtifactCommitFailed, AnalysisTaskStatus.RecoveryRequired).Should().BeTrue();
        JobLifecycle.CanTransition(AnalysisTaskStatus.AuditCommitFailed, AnalysisTaskStatus.RecoveryRequired).Should().BeTrue();
        JobLifecycle.CanTransition(AnalysisTaskStatus.RecoveryRequired, AnalysisTaskStatus.SnapshotCommitted).Should().BeTrue();
        // A recovered task must not skip straight to COMPLETED.
        JobLifecycle.CanTransition(AnalysisTaskStatus.RecoveryRequired, AnalysisTaskStatus.Completed).Should().BeFalse();
    }

    [Fact]
    public void Failure_states_are_classified()
    {
        JobLifecycle.IsFailureState(AnalysisTaskStatus.PreflightFailed).Should().BeTrue();
        JobLifecycle.IsFailureState(AnalysisTaskStatus.EngineFailed).Should().BeTrue();
        JobLifecycle.IsFailureState(AnalysisTaskStatus.AuditCommitFailed).Should().BeTrue();
        JobLifecycle.IsFailureState(AnalysisTaskStatus.CancelledBeforeEngine).Should().BeTrue();
        JobLifecycle.IsFailureState(AnalysisTaskStatus.Completed).Should().BeFalse();
        JobLifecycle.IsFailureState(AnalysisTaskStatus.EngineCompleted).Should().BeFalse();
    }

    [Fact]
    public void Only_completed_may_be_presented_as_fully_complete()
    {
        JobLifecycle.IsFullyCompleted(AnalysisTaskStatus.Completed).Should().BeTrue();
        // Engine done != task done (ADR-OBS-V030-UI-001 原则⑩).
        JobLifecycle.IsFullyCompleted(AnalysisTaskStatus.EngineCompleted).Should().BeFalse();
        JobLifecycle.IsFullyCompleted(AnalysisTaskStatus.AuditCommitted).Should().BeFalse();
        JobLifecycle.IsFullyCompleted(AnalysisTaskStatus.RecoveryRequired).Should().BeFalse();
    }

    [Fact]
    public void Valid_state_set_excludes_legacy_ui_derived_states()
    {
        JobLifecycle.IsValidState(AnalysisTaskStatus.PrecheckPassed).Should().BeTrue();
        JobLifecycle.IsValidState(AnalysisTaskStatus.RecoveryRequired).Should().BeTrue();
        // 'Running' / 'Draft' are legacy creation/UI pre-states, NOT P0-05 committed-chain states.
        JobLifecycle.IsValidState("Running").Should().BeFalse();
        JobLifecycle.IsValidState("Draft").Should().BeFalse();
        JobLifecycle.IsValidState("Reviewed").Should().BeFalse();
    }

    [Fact]
    public void Review_status_values_are_independent_of_job_status()
    {
        JobLifecycle.IsValidReviewStatus(JobLifecycle.ReviewStatus.NotRequired).Should().BeTrue();
        JobLifecycle.IsValidReviewStatus(JobLifecycle.ReviewStatus.Pending).Should().BeTrue();
        JobLifecycle.IsValidReviewStatus(JobLifecycle.ReviewStatus.Reviewed).Should().BeTrue();
        JobLifecycle.IsValidReviewStatus("REVIEWED").Should().BeFalse();
        JobLifecycle.ReviewStatus.All.Should().HaveCount(3);
    }

    [Fact]
    public void In_flight_states_are_those_that_must_be_marked_for_recovery_on_host_exit()
    {
        JobLifecycle.IsInFlight(AnalysisTaskStatus.PrecheckPassed).Should().BeTrue();
        JobLifecycle.IsInFlight(AnalysisTaskStatus.EngineCompleted).Should().BeTrue();
        JobLifecycle.IsInFlight(AnalysisTaskStatus.AuditCommitted).Should().BeTrue();
        JobLifecycle.IsInFlight(AnalysisTaskStatus.Completed).Should().BeFalse();
        JobLifecycle.IsInFlight(AnalysisTaskStatus.RecoveryRequired).Should().BeFalse();
    }
}
