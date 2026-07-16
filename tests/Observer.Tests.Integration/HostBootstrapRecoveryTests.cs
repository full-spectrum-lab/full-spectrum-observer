using System.Collections.Immutable;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.Evidence;
using FullSpectrum.Observer.Recovery;
using FullSpectrum.Observer.Store;
using Xunit;

namespace FullSpectrum.Observer.Tests.Integration;

/// <summary>
/// Host bootstrap → store persistence → recovery round-trip (Module 1). Uses a temporary SQLite
/// database file. Simulates a Host exit driving in-flight tasks to RECOVERY_REQUIRED, then a
/// subsequent start loading those tasks and rebuilding attempts from the stored snapshot.
/// </summary>
public sealed class HostBootstrapRecoveryTests
{
    private static async Task<ObserverStore> OpenTempStoreAsync()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"observer-m1-{System.Guid.NewGuid():N}.db");
        if (File.Exists(dbPath))
        {
            File.Delete(dbPath);
        }
        var store = new ObserverStore(dbPath);
        await store.EnsureSchemaAsync();
        return store;
    }

    private static async Task SeedSubjectAsync(ObserverStore store)
    {
        string now = SystemClock.UtcNow.ToString("O");
        await store.InsertSubjectAsync(new ObservedSubject
        {
            LocalSubjectId = "S-1",
            SubjectType = "PERSON",
            Mode = "OBSERVE",
            ConcentrationTier = null,
            CreatedAt = now,
        });
        await store.InsertSubjectVersionAsync(new SubjectVersion
        {
            VersionId = "SV-1",
            SubjectId = "S-1",
            Status = "Active",
            Seq = 1,
            Payload = "{}",
            SchemaVersion = "1.0.0",
            CreatedAt = now,
            ActiveFrom = now,
            RetiredAt = null,
        });
    }

    private static AnalysisTask MakeTask(string taskId, string status)
    {
        var input = new RawAnalysisInput
        {
            Mode = "FORM",
            CanonicalInput = "{\"x\":1}",
            ContentDigest = new string('d', 64),
            TransformTrace = null,
        };
        return AnalysisTask.Create(taskId, "SV-1", ImmutableArray<string>.Empty, input, "FULL_LOCAL", SystemClock.UtcNow.ToString("O"))
            with { Status = status };
    }

    [Fact]
    public async Task Host_exit_marks_in_flight_tasks_recovery_required_but_leaves_terminal_untouched()
    {
        var store = await OpenTempStoreAsync();
        await SeedSubjectAsync(store);
        await store.InsertAnalysisTaskAsync(MakeTask("TASK-INFLIGHT", AnalysisTaskStatus.PrecheckPassed));
        await store.InsertAnalysisTaskAsync(MakeTask("TASK-DONE", AnalysisTaskStatus.Completed));

        int marked = await RecoveryBootstrap.MarkInFlightTasksForRecoveryAsync(
            store, new SystemClock(), new GuidIdGenerator(), "session-m1");

        marked.Should().Be(1);
        (await store.GetAnalysisTaskAsync("TASK-INFLIGHT"))!.Status.Should().Be(AnalysisTaskStatus.RecoveryRequired);
        (await store.GetAnalysisTaskAsync("TASK-DONE"))!.Status.Should().Be(AnalysisTaskStatus.Completed);
    }

    [Fact]
    public async Task Recovery_round_trip_reruns_engine_when_no_snapshot_persisted()
    {
        var store = await OpenTempStoreAsync();
        await SeedSubjectAsync(store);
        await store.InsertAnalysisTaskAsync(MakeTask("TASK-NO-SNAP", AnalysisTaskStatus.RecoveryRequired));

        var tasks = await store.GetRecoveryRequiredTasksAsync();
        tasks.Should().ContainSingle(t => t.TaskId == "TASK-NO-SNAP");

        AnalysisTask task = (await store.GetAnalysisTaskAsync("TASK-NO-SNAP"))!;
        RecoveryPlan plan = RecoveryPlanner.Build(task, await store.GetRuntimeSnapshotByTaskAsync(task.TaskId));

        plan.EngineRerunRequired.Should().BeTrue();
        plan.Strategy.Should().Be(RecoveryStrategy.ReRunFromSnapshot);
    }

    [Fact]
    public async Task Recovery_round_trip_resumes_post_engine_when_snapshot_persisted()
    {
        var store = await OpenTempStoreAsync();
        await SeedSubjectAsync(store);
        await store.InsertAnalysisTaskAsync(MakeTask("TASK-WITH-SNAP", AnalysisTaskStatus.RecoveryRequired));

        // Persist a result + runtime snapshot (Engine had completed before the interruption).
        await store.InsertAnalysisResultAsync(new AnalysisResult
        {
            ResultId = "RES-1",
            TaskId = "TASK-WITH-SNAP",
            ConclusionPayload = "{}",
            UnknownState = "KNOWN",
            HardGate = false,
            CreatedAt = SystemClock.UtcNow.ToString("O"),
        });
        await store.InsertRuntimeSnapshotAsync(new RuntimeSnapshot
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
        });

        AnalysisTask task = (await store.GetAnalysisTaskAsync("TASK-WITH-SNAP"))!;
        RuntimeSnapshot? snapshot = await store.GetRuntimeSnapshotByTaskAsync(task.TaskId);
        snapshot.Should().NotBeNull();

        RecoveryPlan plan = RecoveryPlanner.Build(task, snapshot);
        plan.EngineRerunRequired.Should().BeFalse();
        plan.Strategy.Should().Be(RecoveryStrategy.ResumePostEngine);
        plan.ResumeFromPhase.Should().Be(RecoveryResumePhase.ArtifactCommitted);
    }

    [Fact]
    public async Task Review_status_is_independent_of_job_status_and_persisted()
    {
        var store = await OpenTempStoreAsync();
        await SeedSubjectAsync(store);
        AnalysisTask task = MakeTask("TASK-REVIEW", AnalysisTaskStatus.AuditCommitted)
            with { ReviewStatus = JobLifecycle.ReviewStatus.Pending };
        await store.InsertAnalysisTaskAsync(task);
        await store.UpdateReviewStatusAsync("TASK-REVIEW", JobLifecycle.ReviewStatus.Reviewed);

        AnalysisTask reloaded = (await store.GetAnalysisTaskAsync("TASK-REVIEW"))!;
        reloaded.Status.Should().Be(AnalysisTaskStatus.AuditCommitted);
        reloaded.ReviewStatus.Should().Be(JobLifecycle.ReviewStatus.Reviewed);
    }
}
