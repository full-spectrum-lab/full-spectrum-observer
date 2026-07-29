using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.Contracts.ReasonCodes;
using FullSpectrum.Observer.EngineFacade;
using FullSpectrum.Observer.Evidence;
using FullSpectrum.Observer.Store;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FullSpectrum.Observer.Tests.Integration;

/// <summary>
/// M3-FIX-04 — Integration coverage for the UnknownState persistence contract end-to-end.
///
/// These tests exercise the REAL <see cref="ObserverStore"/> against a temp SQLite file (no Python
/// Engine required) and the real <see cref="OutputAdapter"/> pass-through, replicating the
/// AnalysisWorkspace R1-D commit chain. They prove:
/// <list type="bullet">
///   <item><description>The previously-failing Web form scenario (UnknownState = "RESOLVED" ->
///     COMMIT_FAILED -> RECOVERY_REQUIRED) is fixed: a contract-valid UNKNOWN now commits and the
///     analysis_results / runtime_snapshot / evidence_bundle / conflict_observations all persist.</description></item>
///   <item><description>The Store accepts UNKNOWN / KNOWN / PARTIAL and rejects RESOLVED (DB CHECK).</description></item>
///   <item><description>The pre-commit gate intercepts an illegal value BEFORE any store write,
///     landing the task in OUTPUT_VALIDATION_FAILED (not RECOVERY_REQUIRED).</description></item>
///   <item><description>The audit chain records RESULT / AUDIT_COMMITTED / COMPLETED, and the same
///     Job is visible after a restart (re-opened store).</description></item>
/// </list>
/// </summary>
public sealed class UnknownStateContractIntegrationTests
{
    private static async Task<(string DbPath, ObserverStore Store)> OpenTempStoreAsync()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"fsp-m3-unk-{Guid.NewGuid():N}.db");
        if (File.Exists(dbPath)) File.Delete(dbPath);
        var store = new ObserverStore(dbPath);
        await store.EnsureSchemaAsync();
        return (dbPath, store);
    }

    private static async Task CleanupAsync(ObserverStore store, string dbPath)
    {
        await store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (File.Exists(dbPath)) File.Delete(dbPath);
    }

    private static async Task DisposeKeepFileAsync(ObserverStore store)
    {
        await store.DisposeAsync();
        SqliteConnection.ClearAllPools();
    }

    private static async Task SeedSubjectAsync(ObserverStore store)
    {
        string now = new SystemClock().UtcNow.ToString("O");
        await store.InsertSubjectAsync(new ObservedSubject
        {
            LocalSubjectId = "S-M3",
            SubjectType = "PERSON",
            Mode = "OBSERVE",
            ConcentrationTier = null,
            CreatedAt = now,
        });
        await store.InsertSubjectVersionAsync(new SubjectVersion
        {
            VersionId = "SV-M3-1",
            SubjectId = "S-M3",
            Status = "Active",
            Seq = 1,
            Payload = "{}",
            SchemaVersion = "1.0.0",
            CreatedAt = now,
            ActiveFrom = now,
            RetiredAt = null,
        });
    }

    private static async Task<string> SeedDraftAsync(ObserverStore store, string taskId, string contentDigest)
    {
        await SeedSubjectAsync(store);
        var input = new RawAnalysisInput
        {
            Mode = "FORM",
            CanonicalInput = "{\"user_question\":\"q\",\"ai_output\":\"a\",\"context\":\"c\"}",
            ContentDigest = contentDigest,
            TransformTrace = null,
        };
        AnalysisTask task = AnalysisTask.Create(
            taskId, "SV-M3-1", ImmutableArray<string>.Empty, input, "SANITIZED_PERSISTENT", "2026-07-12T00:00:00Z");
        await store.InsertAnalysisTaskAsync(task);
        return taskId;
    }

    /// <summary>Builds a complete, Engine-shaped response with the given unknown_state.</summary>
    private static EngineResponse BuildValidResponse(string unknownState, bool withConflicts = true)
    {
        var conflicts = withConflicts
            ? new List<EngineConflictObservation>
            {
                new EngineConflictObservation
                {
                    ConflictType = "KNOWLEDGE_GAP",
                    InvolvedSubjects = new List<string> { "SUBJ-1" },
                    Severity = "MEDIUM",
                    HumanReviewRequired = true,
                    ReasonCode = FoundationReasonCodes.GOV_CONTEXT_INSUFFICIENT,
                    MissingContext = new List<string> { "knowledge:v2" },
                },
            }
            : new List<EngineConflictObservation>();

        return new EngineResponse
        {
            // The frozen Engine v1.5.0 identity is "v1.5.0" (EngineV15Contract.EngineTag); the store
            // CHECK now pins the canonical 'v1.5.0'. M3-FIX-05 / SD-001: never scatter the bare
            // literal — route through EngineVersionContract.CanonicalVersion.
            EngineVersion = EngineVersionContract.CanonicalVersion,
            EngineCommit = EngineV15Contract.EngineCommit,
            SchemaVersion = "1.0.0",
            SchemaDigest = "deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef",
            AnalyzerVersion = EngineV15Contract.AnalyzerVersion,
            ProfileVersion = EngineV15Contract.ProfileVersion,
            Conclusion = JsonSerializer.SerializeToElement(new { result = "ok", score = 0.42 }),
            ConflictObservations = conflicts,
            UnknownState = unknownState,
            HardGate = false,
            RuntimeDigest = new string('a', 64),
            ReplayRef = new EngineReplayRef { Digest = new string('a', 64), EngineVersion = EngineVersionContract.CanonicalVersion },
            Evidence = new EngineEvidence { EvidenceDigest = new string('a', 64), References = new List<string> { "CER/rv1" } },
        };
    }

    /// <summary>
    /// Mirrors the AnalysisWorkspace R1-D commit chain (post ENGINE_COMPLETED / OUTPUT_VALIDATED)
    /// against the REAL store, including the M3-FIX-04 pre-commit contract gate.
    /// </summary>
    private static async Task CommitChainAsync(ObserverStore store, EngineResponse response, string taskId, string now)
    {
        // Pre-commit contract gate (M3-FIX-04): reject illegal unknown_state BEFORE any INSERT.
        UnknownStateContract.ValidateOrThrow(
            response.UnknownState,
            FoundationReasonCodes.INVALID_UNKNOWN_STATE_CONTRACT,
            "analysis_results.unknown_state is not a legal contract value (UNKNOWN / KNOWN / PARTIAL).");

        AnalysisTask? task = await store.GetAnalysisTaskAsync(taskId);
        task.Should().NotBeNull();
        AnalysisOutput output = new OutputAdapter().Parse(response, task!, now);

        await store.InsertAnalysisResultAsync(output.Result);
        await store.InsertRuntimeSnapshotAsync(output.Snapshot);
        await store.UpdateAnalysisTaskStatusAsync(taskId, AnalysisTaskStatus.SnapshotCommitted);
        await store.AppendAuditAsync(AuditRecord.Append("AUD-001", taskId, "SNAPSHOT_COMMITTED", "t", "m", "s", now, "d", null));

        await store.InsertEvidenceBundleAsync(output.Evidence);
        await store.UpdateAnalysisTaskStatusAsync(taskId, AnalysisTaskStatus.ArtifactCommitted);
        await store.AppendAuditAsync(AuditRecord.Append("AUD-002", taskId, "ARTIFACT_COMMITTED", "t", "m", "s", now, "d", null));

        await store.InsertConflictObservationsAsync(output.Conflicts);
        await store.UpdateAnalysisTaskStatusAsync(taskId, AnalysisTaskStatus.ObservationCommitted);
        await store.AppendAuditAsync(AuditRecord.Append("AUD-003", taskId, "OBSERVATION_COMMITTED", "t", "m", "s", now, "d", null));

        await store.AppendAuditAsync(AuditRecord.Append("AUD-004", taskId, "RESULT", "t", "m", "s", now, "d", null));
        await store.UpdateAnalysisTaskStatusAsync(taskId, AnalysisTaskStatus.AuditCommitted);
        await store.AppendAuditAsync(AuditRecord.Append("AUD-005", taskId, "AUDIT_COMMITTED", "t", "m", "s", now, "d", null));

        await store.UpdateAnalysisTaskStatusAsync(taskId, AnalysisTaskStatus.Completed);
        await store.AppendAuditAsync(AuditRecord.Append("AUD-006", taskId, "COMPLETED", "t", "m", "s", now, "d", null));
    }

    /// <summary>Mirrors the AnalysisWorkspace M3-FIX-04 pre-commit gate (returns the resulting status).</summary>
    private static async Task<string> RunPreCommitGateAsync(ObserverStore store, EngineResponse response, string taskId, string now)
    {
        if (!UnknownStateContract.IsValid(response.UnknownState))
        {
            await store.AppendAuditAsync(AuditRecord.Append("AUD-G", taskId, "OUTPUT_VALIDATION_REJECTED", "t", "m", "s", now, "d", null));
            await store.UpdateAnalysisTaskStatusAsync(taskId, AnalysisTaskStatus.OutputValidationFailed);
            return AnalysisTaskStatus.OutputValidationFailed;
        }
        return "OK";
    }

    // ---- 7. OutputAdapter pass-through -----------------------------------

    [Fact]
    public void OutputAdapter_passes_through_legal_unknown_state_verbatim()
    {
        foreach (string legal in new[] { UnknownStateContract.Unknown, UnknownStateContract.Known, UnknownStateContract.Partial })
        {
            EngineResponse response = BuildValidResponse(legal, withConflicts: false);
            var task = new AnalysisTask
            {
                TaskId = "TASK-PROBE",
                SubjectVersionId = "SV-M3-1",
                KnowledgeVersionIds = ImmutableArray<string>.Empty,
                InputMode = "FORM",
                CanonicalInput = "{}",
                ContentDigest = "d",
                RetentionMode = "SANITIZED_PERSISTENT",
                Status = AnalysisTaskStatus.Draft,
                CreatedAt = "2026-07-12T00:00:00Z",
            };
            AnalysisOutput output = new OutputAdapter().Parse(response, task, "2026-07-12T00:00:00Z");
            output.Result.UnknownState.Should().Be(legal);
        }
    }

    // ---- 8-10. Store accepts legal values ---------------------------------

    [Theory]
    [InlineData(UnknownStateContract.Unknown)]
    [InlineData(UnknownStateContract.Known)]
    [InlineData(UnknownStateContract.Partial)]
    public async Task Store_accepts_legal_unknown_state(string legal)
    {
        (string dbPath, ObserverStore store) = await OpenTempStoreAsync();
        try
        {
            string taskId = await SeedDraftAsync(store, "TASK-M3-STORE-" + legal, "digest-" + legal);
            var result = new AnalysisResult
            {
                ResultId = $"RES-{taskId}",
                TaskId = taskId,
                ConclusionPayload = "{}",
                UnknownState = legal,
                HardGate = false,
                CreatedAt = "2026-07-12T00:00:00Z",
            };
            Func<Task> act = async () => await store.InsertAnalysisResultAsync(result);
            await act.Should().NotThrowAsync();
            (await store.GetAnalysisResultByTaskAsync(taskId))!.UnknownState.Should().Be(legal);
        }
        finally { await CleanupAsync(store, dbPath); }
    }

    // ---- 11. Store rejects RESOLVED (DB CHECK) ----------------------------

    [Fact]
    public async Task Store_rejects_RESOLVED_unknown_state()
    {
        (string dbPath, ObserverStore store) = await OpenTempStoreAsync();
        try
        {
            string taskId = await SeedDraftAsync(store, "TASK-M3-BAD", "digest-bad");
            var bad = new AnalysisResult
            {
                ResultId = $"RES-{taskId}",
                TaskId = taskId,
                ConclusionPayload = "{}",
                UnknownState = "RESOLVED",
                HardGate = false,
                CreatedAt = "2026-07-12T00:00:00Z",
            };
            Func<Task> act = async () => await store.InsertAnalysisResultAsync(bad);
            await act.Should().ThrowAsync<Exception>().WithMessage("*CHECK*");
        }
        finally { await CleanupAsync(store, dbPath); }
    }

    // ---- 12. Illegal state blocked BEFORE store ---------------------------

    [Fact]
    public async Task Illegal_unknown_state_is_blocked_before_store_write()
    {
        (string dbPath, ObserverStore store) = await OpenTempStoreAsync();
        try
        {
            string taskId = await SeedDraftAsync(store, "TASK-M3-INT", "digest-int");
            string now = "2026-07-12T00:00:00Z";
            EngineResponse response = BuildValidResponse("RESOLVED");

            string gate = await RunPreCommitGateAsync(store, response, taskId, now);
            gate.Should().Be(AnalysisTaskStatus.OutputValidationFailed);

            // No analysis_results row was ever written.
            (await store.GetAnalysisResultByTaskAsync(taskId)).Should().BeNull();
            AnalysisTask? after = await store.GetAnalysisTaskAsync(taskId);
            after!.Status.Should().Be(AnalysisTaskStatus.OutputValidationFailed);
            after.Status.Should().NotBe(AnalysisTaskStatus.RecoveryRequired);
        }
        finally { await CleanupAsync(store, dbPath); }
    }

    // ---- Bonus: EngineFacade policy emits a contract-valid UNKNOWN --------

    [Fact]
    public void EngineFacade_policy_defaults_to_contract_valid_UNKOWN()
    {
        string derived = UnknownStateContract.FromVerbatimOrFailClosed(null).Value;
        UnknownStateContract.IsValid(derived).Should().BeTrue();
        derived.Should().Be(UnknownStateContract.Unknown);
    }

    // ---- 13. Web form scenario now completes ------------------------------

    [Fact]
    public async Task Web_form_scenario_completes_after_fix_with_valid_unknown_state()
    {
        (string dbPath, ObserverStore store) = await OpenTempStoreAsync();
        try
        {
            string taskId = await SeedDraftAsync(store, "TASK-M3-001", "digest-m3-001");
            string now = "2026-07-12T00:00:00Z";
            EngineResponse response = BuildValidResponse(UnknownStateContract.Unknown);

            UnknownStateContract.IsValid(response.UnknownState).Should().BeTrue();
            await CommitChainAsync(store, response, taskId, now);

            AnalysisTask? persisted = await store.GetAnalysisTaskAsync(taskId);
            persisted.Should().NotBeNull();
            persisted!.Status.Should().Be(AnalysisTaskStatus.Completed);
            JobLifecycle.IsFullyCompleted(persisted.Status).Should().BeTrue();
        }
        finally { await CleanupAsync(store, dbPath); }
    }

    // ---- 14-17. Artifacts persist -----------------------------------------

    [Fact]
    public async Task analysis_results_persisted_with_valid_unknown_state()
    {
        (string dbPath, ObserverStore store) = await OpenTempStoreAsync();
        try
        {
            string taskId = await SeedDraftAsync(store, "TASK-M3-002", "digest-m3-002");
            await CommitChainAsync(store, BuildValidResponse(UnknownStateContract.Unknown), taskId, "2026-07-12T00:00:00Z");

            AnalysisResult? result = await store.GetAnalysisResultByTaskAsync(taskId);
            result.Should().NotBeNull();
            result!.UnknownState.Should().Be(UnknownStateContract.Unknown);
            result.HardGate.Should().BeFalse();
        }
        finally { await CleanupAsync(store, dbPath); }
    }

    [Fact]
    public async Task runtime_snapshot_persisted_with_pinned_engine_version()
    {
        (string dbPath, ObserverStore store) = await OpenTempStoreAsync();
        try
        {
            string taskId = await SeedDraftAsync(store, "TASK-M3-003", "digest-m3-003");
            await CommitChainAsync(store, BuildValidResponse(UnknownStateContract.Unknown), taskId, "2026-07-12T00:00:00Z");

            RuntimeSnapshot? snapshot = await store.GetRuntimeSnapshotByResultAsync($"RES-{taskId}");
            snapshot.Should().NotBeNull();
            snapshot!.EngineVersion.Should().Be(EngineVersionContract.CanonicalVersion);
        }
        finally { await CleanupAsync(store, dbPath); }
    }

    [Fact]
    public async Task evidence_bundle_persisted()
    {
        (string dbPath, ObserverStore store) = await OpenTempStoreAsync();
        try
        {
            string taskId = await SeedDraftAsync(store, "TASK-M3-004", "digest-m3-004");
            await CommitChainAsync(store, BuildValidResponse(UnknownStateContract.Unknown), taskId, "2026-07-12T00:00:00Z");

            EvidenceBundle? evidence = await store.GetEvidenceBundleByResultAsync($"RES-{taskId}");
            evidence.Should().NotBeNull();
            evidence!.EvidenceDigest.Should().Be(new string('a', 64));
        }
        finally { await CleanupAsync(store, dbPath); }
    }

    [Fact]
    public async Task conflict_observation_persisted_by_rule()
    {
        (string dbPath, ObserverStore store) = await OpenTempStoreAsync();
        try
        {
            string taskId = await SeedDraftAsync(store, "TASK-M3-005", "digest-m3-005");
            await CommitChainAsync(store, BuildValidResponse(UnknownStateContract.Unknown), taskId, "2026-07-12T00:00:00Z");

            List<ConflictObservation> conflicts = await store.GetConflictObservationsByResultAsync($"RES-{taskId}");
            conflicts.Should().HaveCount(1);
            conflicts[0].ConflictType.Should().Be("KNOWLEDGE_GAP");
            conflicts[0].MissingContext.Should().NotBeNull();
            conflicts[0].MissingContext!.Value.Should().Contain("knowledge:v2");
            conflicts[0].HumanReviewRequired.Should().BeTrue();
        }
        finally { await CleanupAsync(store, dbPath); }
    }

    // ---- 18. Audit chain contains RESULT / AUDIT_COMMITTED / COMPLETED ----

    [Fact]
    public async Task Audit_chain_records_RESULT_and_COMPLETED_and_no_RECOVERY()
    {
        (string dbPath, ObserverStore store) = await OpenTempStoreAsync();
        try
        {
            string taskId = await SeedDraftAsync(store, "TASK-M3-006", "digest-m3-006");
            await CommitChainAsync(store, BuildValidResponse(UnknownStateContract.Unknown), taskId, "2026-07-12T00:00:00Z");

            List<AuditRecord> chain = await store.GetAuditChainAsync(taskId);
            var actions = chain.ConvertAll(a => a.Action);
            actions.Should().Contain("RESULT");
            actions.Should().Contain("AUDIT_COMMITTED");
            actions.Should().Contain("COMPLETED");
            actions.Should().NotContain("RECOVERY_REQUIRED");
            actions.Should().NotContain("COMMIT_FAILED");
        }
        finally { await CleanupAsync(store, dbPath); }
    }

    // ---- 19. Restart still shows the same Job -----------------------------

    [Fact]
    public async Task Restart_reopens_same_job_and_shows_COMPLETED()
    {
        (string dbPath, ObserverStore store) = await OpenTempStoreAsync();
        string taskId;
        try
        {
            taskId = await SeedDraftAsync(store, "TASK-M3-RES", "digest-m3-res");
            await CommitChainAsync(store, BuildValidResponse(UnknownStateContract.Unknown), taskId, "2026-07-12T00:00:00Z");
        }
        finally
        {
            await DisposeKeepFileAsync(store); // keep the file to simulate a Host restart
        }

        var reopened = new ObserverStore(dbPath);
        await reopened.EnsureSchemaAsync();
        try
        {
            AnalysisTask? reloaded = await reopened.GetAnalysisTaskAsync(taskId);
            reloaded.Should().NotBeNull();
            reloaded!.Status.Should().Be(AnalysisTaskStatus.Completed);
            (await reopened.GetAnalysisResultByTaskAsync(taskId)).Should().NotBeNull();
        }
        finally
        {
            await CleanupAsync(reopened, dbPath);
        }
    }

    // ---- 20. Recovery path still reaches COMPLETED ------------------------

    [Fact]
    public async Task Recovery_re_entry_after_failure_still_reaches_COMPLETED()
    {
        (string dbPath, ObserverStore store) = await OpenTempStoreAsync();
        try
        {
            string taskId = await SeedDraftAsync(store, "TASK-M3-REC", "digest-m3-rec");
            string now = "2026-07-12T00:00:00Z";

            // A prior commit failure had driven the task to RECOVERY_REQUIRED (the legacy
            // COMMIT_FAILED -> RECOVERY_REQUIRED failure the fix removes at the source).
            await store.UpdateAnalysisTaskStatusAsync(taskId, AnalysisTaskStatus.RecoveryRequired);
            (await store.GetAnalysisTaskAsync(taskId))!.Status.Should().Be(AnalysisTaskStatus.RecoveryRequired);

            // Recovery re-enters the R1-D chain with a NOW-valid unknown_state (UNKNOWN) and completes.
            EngineResponse response = BuildValidResponse(UnknownStateContract.Unknown);
            UnknownStateContract.IsValid(response.UnknownState).Should().BeTrue();
            await CommitChainAsync(store, response, taskId, now);

            AnalysisTask? persisted = await store.GetAnalysisTaskAsync(taskId);
            persisted!.Status.Should().Be(AnalysisTaskStatus.Completed);
            JobLifecycle.IsRecoveryState(persisted.Status).Should().BeFalse();
        }
        finally { await CleanupAsync(store, dbPath); }
    }
}
