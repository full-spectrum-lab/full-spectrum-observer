using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.Evidence;
using FullSpectrum.Observer.Store;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FullSpectrum.Observer.Tests.Integration;

/// <summary>
/// M2 P1 main line — analysis task lifecycle closure at the persistence + state-machine boundary.
///
/// Authority red lines verified here:
/// <list type="bullet">
///   <item><description>The SQLite store (JobId primary key) is the SINGLE source of truth.</description></item>
///   <item><description>Every status transition is forward-only, validated by <see cref="JobLifecycle.CanAdvance"/>.</description></item>
///   <item><description>Each transition is recorded as an append-only audit event.</description></item>
///   <item><description>Recovery re-enters the chain at SNAPSHOT_COMMITTED and still reaches COMPLETED.</description></item>
///   <item><description>Idempotency is decided by the persisted content digest (JobId + fingerprint).</description></item>
/// </list>
///
/// These tests exercise the REAL <see cref="ObserverStore"/> against a temp SQLite file — no Python
/// Engine is required, so they run in any CI without the private runtime. The store is created and
/// disposed locally per test (mirroring HostBootstrapRecoveryTests) so the class owns no disposable
/// field (CA1001).
/// </summary>
public sealed class AnalysisLifecycleClosureTests
{
    private static async Task<(string DbPath, ObserverStore Store)> OpenTempStoreAsync()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"fsp-m2-lifecycle-{Guid.NewGuid():N}.db");
        if (File.Exists(dbPath)) File.Delete(dbPath);
        var store = new ObserverStore(dbPath);
        await store.EnsureSchemaAsync();
        return (dbPath, store);
    }

    /// <summary>Releases the SQLite connection pool (Microsoft.Data.Sqlite pools per-process,
    /// keeping the file handle open even after per-call <c>using</c> disposal) so the temp file can
    /// be deleted without an <see cref="IOException"/>. Mirrors the behavior the app relies on when
    /// the Host shuts down.</summary>
    private static async Task CleanupAsync(ObserverStore store, string dbPath)
    {
        await store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (File.Exists(dbPath)) File.Delete(dbPath);
    }

    /// <summary>Mirrors <c>AnalysisWorkspace.TransitionAsync</c>: validate forward-only, persist,
    /// then record an append-only audit event.</summary>
    private static async Task AdvanceAsync(ObserverStore store, string taskId, string nextStatus, string action, int auditSeq)
    {
        AnalysisTask? current = await store.GetAnalysisTaskAsync(taskId);
        string currentStatus = current?.Status ?? AnalysisTaskStatus.Draft;
        if (!JobLifecycle.CanAdvance(currentStatus, nextStatus))
        {
            throw new InvalidOperationException($"Illegal forward-only transition: {currentStatus} -> {nextStatus}.");
        }
        await store.UpdateAnalysisTaskStatusAsync(taskId, nextStatus);
        await store.AppendAuditAsync(AuditRecord.Append(
            $"AUD-{auditSeq:D4}", taskId, action, "m2-test", "m2-test", "m2-test",
            "2026-07-12T00:00:00Z", "digest", null));
    }

    private static async Task<string> SeedDraftAsync(ObserverStore store, string taskId, string contentDigest)
    {
        // The analysis_tasks.subject_version_id FK requires a real subject version to exist
        // (mirrors the Web orchestration, which always binds a persisted subject version).
        await SeedSubjectAsync(store);
        var input = new RawAnalysisInput
        {
            Mode = "FORM",
            CanonicalInput = "{\"user_question\":\"q\",\"ai_output\":\"a\",\"context\":\"c\"}",
            ContentDigest = contentDigest,
            TransformTrace = null,
        };
        AnalysisTask task = AnalysisTask.Create(
            taskId, "SV-M2-1", ImmutableArray<string>.Empty, input, "SANITIZED_PERSISTENT", "2026-07-12T00:00:00Z");
        await store.InsertAnalysisTaskAsync(task);
        return taskId;
    }

    /// <summary>Seeds the subject + subject version that the analysis task's FK
    /// (<c>analysis_tasks.subject_version_id</c>) requires. Mirrors
    /// <c>HostBootstrapRecoveryTests.SeedSubjectAsync</c>.</summary>
    private static async Task SeedSubjectAsync(ObserverStore store)
    {
        string now = new SystemClock().UtcNow.ToString("O");
        await store.InsertSubjectAsync(new ObservedSubject
        {
            LocalSubjectId = "S-M2",
            SubjectType = "PERSON",
            Mode = "OBSERVE",
            ConcentrationTier = null,
            CreatedAt = now,
        });
        await store.InsertSubjectVersionAsync(new SubjectVersion
        {
            VersionId = "SV-M2-1",
            SubjectId = "S-M2",
            Status = "Active",
            Seq = 1,
            Payload = "{}",
            SchemaVersion = "1.0.0",
            CreatedAt = now,
            ActiveFrom = now,
            RetiredAt = null,
        });
    }

    [Fact]
    public async Task Full_commit_chain_reaches_COMPLETED_and_persists_to_sqlite()
    {
        (string dbPath, ObserverStore store) = await OpenTempStoreAsync();
        try
        {
            string taskId = await SeedDraftAsync(store, "TASK-M2-001", "digest-001");
            int seq = 0;
            await AdvanceAsync(store, taskId, AnalysisTaskStatus.PrecheckPassed, "PRECHECK_PASSED", ++seq);
            await AdvanceAsync(store, taskId, AnalysisTaskStatus.EngineCompleted, "ENGINE_COMPLETED", ++seq);
            await AdvanceAsync(store, taskId, AnalysisTaskStatus.OutputValidated, "OUTPUT_VALIDATED", ++seq);
            await AdvanceAsync(store, taskId, AnalysisTaskStatus.SnapshotCommitted, "SNAPSHOT_COMMITTED", ++seq);
            await AdvanceAsync(store, taskId, AnalysisTaskStatus.ArtifactCommitted, "ARTIFACT_COMMITTED", ++seq);
            await AdvanceAsync(store, taskId, AnalysisTaskStatus.ObservationCommitted, "OBSERVATION_COMMITTED", ++seq);
            await AdvanceAsync(store, taskId, AnalysisTaskStatus.AuditCommitted, "AUDIT_COMMITTED", ++seq);
            await AdvanceAsync(store, taskId, AnalysisTaskStatus.Completed, "COMPLETED", ++seq);

            AnalysisTask? persisted = await store.GetAnalysisTaskAsync(taskId);
            persisted.Should().NotBeNull();
            persisted!.Status.Should().Be(AnalysisTaskStatus.Completed);
            JobLifecycle.IsFullyCompleted(persisted.Status).Should().BeTrue();
            JobLifecycle.IsInProgress(persisted.Status).Should().BeFalse();

            // One append-only audit event per transition.
            (await store.GetAuditChainAsync(taskId)).Should().HaveCount(8);
        }
        finally
        {
            await CleanupAsync(store, dbPath);
        }
    }

    [Fact]
    public async Task Backward_transition_is_refused_by_the_forward_only_guard()
    {
        (string dbPath, ObserverStore store) = await OpenTempStoreAsync();
        try
        {
            string taskId = await SeedDraftAsync(store, "TASK-M2-002", "digest-002");
            await AdvanceAsync(store, taskId, AnalysisTaskStatus.PrecheckPassed, "PRECHECK_PASSED", 1);
            await AdvanceAsync(store, taskId, AnalysisTaskStatus.EngineCompleted, "ENGINE_COMPLETED", 2);

            var act = () => AdvanceAsync(store, taskId, AnalysisTaskStatus.PrecheckPassed, "ILLEGAL_BACK", 3);
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*Illegal forward-only transition*");

            // The persisted status must be unchanged (still ENGINE_COMPLETED) — no partial write.
            (await store.GetAnalysisTaskAsync(taskId))!.Status.Should().Be(AnalysisTaskStatus.EngineCompleted);
        }
        finally
        {
            await CleanupAsync(store, dbPath);
        }
    }

    [Fact]
    public async Task Failure_then_recovery_re_enters_chain_and_reaches_COMPLETED()
    {
        (string dbPath, ObserverStore store) = await OpenTempStoreAsync();
        try
        {
            string taskId = await SeedDraftAsync(store, "TASK-M2-003", "digest-003");
            int seq = 0;
            await AdvanceAsync(store, taskId, AnalysisTaskStatus.PrecheckPassed, "PRECHECK_PASSED", ++seq);
            await AdvanceAsync(store, taskId, AnalysisTaskStatus.EngineCompleted, "ENGINE_COMPLETED", ++seq);
            // A commit failure drives the task to RECOVERY_REQUIRED.
            await AdvanceAsync(store, taskId, AnalysisTaskStatus.RecoveryRequired, "RECOVERY_REQUIRED", ++seq);
            // Recovery re-enters at SNAPSHOT_COMMITTED and continues the commit chain.
            await AdvanceAsync(store, taskId, AnalysisTaskStatus.SnapshotCommitted, "SNAPSHOT_COMMITTED", ++seq);
            await AdvanceAsync(store, taskId, AnalysisTaskStatus.ArtifactCommitted, "ARTIFACT_COMMITTED", ++seq);
            await AdvanceAsync(store, taskId, AnalysisTaskStatus.ObservationCommitted, "OBSERVATION_COMMITTED", ++seq);
            await AdvanceAsync(store, taskId, AnalysisTaskStatus.AuditCommitted, "AUDIT_COMMITTED", ++seq);
            await AdvanceAsync(store, taskId, AnalysisTaskStatus.Completed, "COMPLETED", ++seq);

            AnalysisTask? persisted = await store.GetAnalysisTaskAsync(taskId);
            persisted!.Status.Should().Be(AnalysisTaskStatus.Completed);
            JobLifecycle.IsRecoveryState(persisted.Status).Should().BeFalse();
        }
        finally
        {
            await CleanupAsync(store, dbPath);
        }
    }

    [Fact]
    public async Task Idempotency_decision_against_persisted_content_digest_is_hit_then_conflict()
    {
        (string dbPath, ObserverStore store) = await OpenTempStoreAsync();
        try
        {
            string taskId = await SeedDraftAsync(store, "TASK-M2-004", "digest-004");
            AnalysisTask? existing = await store.GetAnalysisTaskAsync(taskId);
            existing.Should().NotBeNull();

            JobIdempotency.Decide(existing!.ContentDigest, "digest-004")
                .Should().Be(JobIdempotency.Outcome.Hit);
            JobIdempotency.Decide(existing.ContentDigest, "digest-OTHER")
                .Should().Be(JobIdempotency.Outcome.Conflict);
        }
        finally
        {
            await CleanupAsync(store, dbPath);
        }
    }
}
