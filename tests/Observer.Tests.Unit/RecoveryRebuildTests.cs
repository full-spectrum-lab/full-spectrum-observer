using System.Collections.Immutable;
using FluentAssertions;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.Recovery;
using Xunit;

namespace FullSpectrum.Observer.Tests.Unit;

/// <summary>
/// Recovery rebuild logic (P0-05 §3 / P0-B rule 3). The planner must decide, from the stored
/// Runtime Snapshot, whether the pinned Engine must re-run (interrupted before it persisted) or
/// the task can resume post-Engine without recomputing the Engine output — and it must pin the
/// ORIGINAL version bindings (never the current default configuration).
/// </summary>
public sealed class RecoveryRebuildTests
{
    private static AnalysisTask MakeTask(string status) =>
        AnalysisTask.Create(
            "TASK-1", "SV-1", ImmutableArray.Create("KV-1", "KV-2"),
            new RawAnalysisInput { Mode = "FORM", CanonicalInput = "{\"x\":1}", ContentDigest = new string('d', 64) },
            "FULL_LOCAL", "2026-07-16T00:00:00Z")
            with { Status = status };

    private static RuntimeSnapshot MakeSnapshot() => new()
    {
        SnapshotId = "SNP-1",
        ResultId = "RES-1",
        AnalyzerVersion = "1.5.0",
        EngineVersion = "1.5.0",
        ProfileVersion = "1.0.0",
        SchemaVersion = "1.0.0",
        InputDigest = new string('a', 64),
        ConfigDigest = new string('b', 64),
        RuntimeDigest = new string('c', 64),
    };

    [Fact]
    public void Interrupted_before_engine_persisted_reruns_pinned_engine_from_original_snapshot()
    {
        AnalysisTask task = MakeTask(AnalysisTaskStatus.RecoveryRequired);

        RecoveryPlan plan = RecoveryPlanner.Build(task, snapshot: null);

        plan.Strategy.Should().Be(RecoveryStrategy.ReRunFromSnapshot);
        plan.EngineRerunRequired.Should().BeTrue();
        plan.ResumeFromPhase.Should().Be(RecoveryResumePhase.SnapshotCommitted);
        plan.HasSnapshot.Should().BeFalse();
    }

    [Fact]
    public void Interrupted_after_engine_completed_resumes_downstream_without_rerunning_engine()
    {
        AnalysisTask task = MakeTask(AnalysisTaskStatus.RecoveryRequired);

        RecoveryPlan plan = RecoveryPlanner.Build(task, MakeSnapshot());

        plan.Strategy.Should().Be(RecoveryStrategy.ResumePostEngine);
        plan.EngineRerunRequired.Should().BeFalse();
        plan.ResumeFromPhase.Should().Be(RecoveryResumePhase.ArtifactCommitted);
        plan.HasSnapshot.Should().BeTrue();
    }

    [Fact]
    public void Recovery_plan_locks_original_version_bindings_verbatim()
    {
        AnalysisTask task = MakeTask(AnalysisTaskStatus.RecoveryRequired);
        RuntimeSnapshot snapshot = MakeSnapshot();

        RecoveryPlan plan = RecoveryPlanner.Build(task, snapshot);

        // The rebuild must use the stored task's bindings, never "current default" values (P0-05 规则⑤).
        plan.SubjectVersionId.Should().Be("SV-1");
        plan.KnowledgeVersionIds.Should().BeEquivalentTo(ImmutableArray.Create("KV-1", "KV-2"));
        plan.CanonicalInput.Should().Be("{\"x\":1}");
        plan.ContentDigest.Should().Be(new string('d', 64));
        plan.Snapshot.Should().NotBeNull();
        plan.Snapshot!.EngineVersion.Should().Be("1.5.0");
    }

    [Fact]
    public void Planning_is_only_allowed_for_recovery_required_tasks()
    {
        AnalysisTask completed = MakeTask(AnalysisTaskStatus.Completed);
        AnalysisTask engineDone = MakeTask(AnalysisTaskStatus.EngineCompleted);

        var forCompleted = () => RecoveryPlanner.Build(completed, null);
        var forEngineDone = () => RecoveryPlanner.Build(engineDone, null);

        forCompleted.Should().Throw<InvalidOperationException>().WithMessage("*RECOVERY_REQUIRED*");
        forEngineDone.Should().Throw<InvalidOperationException>().WithMessage("*RECOVERY_REQUIRED*");
    }

    [Fact]
    public void Recovery_required_task_without_snapshot_is_rerun_eligible()
    {
        AnalysisTask task = MakeTask(AnalysisTaskStatus.RecoveryRequired);
        RecoveryPlan plan = RecoveryPlanner.Build(task, null);

        plan.EngineRerunRequired.Should().BeTrue();
        plan.Snapshot.Should().BeNull();
    }
}
