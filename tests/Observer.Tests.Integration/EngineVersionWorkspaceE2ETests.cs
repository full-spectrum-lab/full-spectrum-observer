using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.Contracts.ReasonCodes;
using FullSpectrum.Observer.EngineFacade;
using FullSpectrum.Observer.Host.Web.Services;
using FullSpectrum.Observer.Store;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FullSpectrum.Observer.Tests.Integration;

/// <summary>
/// M3-FIX-05 / SD-001 — End-to-end coverage of the AnalysisWorkspace R1-D commit chain driven by a
/// fake Engine (an <see cref="IEngineFacade"/> test double) returning a canned, contract-valid
/// response. This exercises the REAL workspace, the REAL <see cref="ObserverStore"/>, and the REAL
/// pre-commit contract gates (both UnknownState and the new Engine-version gate) — proving the Web
/// FORM scenario reaches COMPLETED with the canonical engine_version "v1.5.0", and that an illegal
/// Engine version is intercepted BEFORE any SQLite write (never surfacing as COMMIT_FAILED ->
/// RECOVERY_REQUIRED).
///
/// <para>NOTE: per DET-001 (OPEN), these tests NEVER assert RAW_OUTPUT_SHA256 equality; they only
/// verify the verbatim pass-through of the fake Engine's digests.</para>
/// </summary>
public sealed class EngineVersionWorkspaceE2ETests
{
    /// <summary>A fake Engine facade returning a single canned response (no Python worker).</summary>
    private sealed class FakeEngineFacade : IEngineFacade
    {
        private readonly EngineResponse _response;
        public FakeEngineFacade(EngineResponse response) => _response = response;
        public Task<EngineResponse> AnalyzeAsync(EngineRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(_response);
    }

    private static EngineResponse BuildResponse(string engineVersion, string unknownState, bool withConflicts = true)
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
            // Canonical / legacy / illegal Engine version is injected by the caller.
            EngineVersion = engineVersion,
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
            ReplayRef = new EngineReplayRef { Digest = new string('a', 64), EngineVersion = engineVersion },
            Evidence = new EngineEvidence { EvidenceDigest = new string('a', 64), References = new List<string> { "CER/rv1" } },
        };
    }

    private static async Task<(string DbPath, ObserverStore Store, AnalysisWorkspace Workspace)> BuildWorkspaceAsync(EngineResponse response)
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"fsp-e2e-{Guid.NewGuid():N}.db");
        if (File.Exists(dbPath)) File.Delete(dbPath);
        var store = new ObserverStore(dbPath);
        await store.EnsureSchemaAsync();

        var auditContext = new AuditContext();
        var auditViewer = new AuditViewer(store, auditContext);
        var subjects = new SubjectCatalog(store, auditContext, auditViewer);
        var knowledge = new KnowledgeCatalog(store, auditContext, auditViewer);
        var shutdown = new AnalysisShutdownToken();
        var intake = new IntakeAdapter();
        var output = new OutputAdapter();
        IEngineFacade engine = new FakeEngineFacade(response);
        var workspace = new AnalysisWorkspace(store, engine, intake, output, auditViewer, subjects, knowledge, shutdown);

        string now = DateTime.UtcNow.ToString("O");
        await store.InsertSubjectAsync(new ObservedSubject
        {
            LocalSubjectId = "S-E2E",
            SubjectType = "PERSON",
            Mode = "OBSERVE",
            ConcentrationTier = null,
            CreatedAt = now,
        });
        await store.InsertSubjectVersionAsync(new SubjectVersion
        {
            VersionId = "SV-E2E",
            SubjectId = "S-E2E",
            Status = "Active",
            Seq = 1,
            Payload = "{}",
            SchemaVersion = "1.0.0",
            CreatedAt = now,
            ActiveFrom = now,
            RetiredAt = null,
        });

        return (dbPath, store, workspace);
    }

    private static async Task<AnalysisRunOutcome> RunAsync(
        EngineResponse response, string engineVersion, string unknownState, bool withConflicts = true)
    {
        (string dbPath, ObserverStore store, AnalysisWorkspace workspace) = await BuildWorkspaceAsync(response);
        // The engine version / unknown state injected here drive the run; we rebuild the response with
        // the requested values for clarity.
        EngineResponse effective = BuildResponse(engineVersion, unknownState, withConflicts);
        var input = new RawAnalysisInput
        {
            Mode = "FORM",
            CanonicalInput = "{\"user_question\":\"q\",\"ai_output\":\"a\",\"context\":\"c\"}",
            ContentDigest = "digest-e2e-" + Guid.NewGuid().ToString("N"),
            TransformTrace = null,
        };
        AnalysisRunOutcome outcome = await workspace.CreateAndRunAsync(
            null, "SV-E2E", ImmutableArray<string>.Empty, input, RetentionMode.SanitizedPersistent);
        // Stash the store/dbPath on the outcome via the test-local closure is not possible; tests read
        // from the returned tuple instead. We return the outcome and let callers read via store.
        _lastStore = store;
        _lastDbPath = dbPath;
        return outcome;
    }

    private static ObserverStore? _lastStore;
    private static string? _lastDbPath;

    private static async Task CleanupLastAsync()
    {
        if (_lastStore is not null) await _lastStore.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (_lastDbPath is not null && File.Exists(_lastDbPath)) File.Delete(_lastDbPath);
        _lastStore = null;
        _lastDbPath = null;
    }

    // ---- 14. Web FORM scenario reaches COMPLETED with canonical engine version ----

    [Fact]
    public async Task Web_form_scenario_reaches_COMPLETED_with_canonical_engine_version()
    {
        AnalysisRunOutcome outcome = await RunAsync(
            BuildResponse(EngineVersionContract.CanonicalVersion, UnknownStateContract.Unknown),
            EngineVersionContract.CanonicalVersion,
            UnknownStateContract.Unknown);
        try
        {
            outcome.Succeeded.Should().BeTrue();
            outcome.Task.Should().NotBeNull();
            outcome.Task!.Status.Should().Be(AnalysisTaskStatus.Completed);
            JobLifecycle.IsFullyCompleted(outcome.Task.Status).Should().BeTrue();
        }
        finally { await CleanupLastAsync(); }
    }

    // ---- 10/11. Runtime Snapshot writes AND reads the canonical "v1.5.0" ----

    [Fact]
    public async Task Runtime_snapshot_writes_and_reads_canonical_v1_5_0()
    {
        AnalysisRunOutcome outcome = await RunAsync(
            BuildResponse(EngineVersionContract.CanonicalVersion, UnknownStateContract.Unknown),
            EngineVersionContract.CanonicalVersion,
            UnknownStateContract.Unknown);
        try
        {
            string resultId = "RES-" + outcome.Task!.TaskId;
            RuntimeSnapshot? snapshot = await _lastStore!.GetRuntimeSnapshotByResultAsync(resultId);
            snapshot.Should().NotBeNull();
            snapshot!.EngineVersion.Should().Be(EngineVersionContract.CanonicalVersion);
            snapshot.EngineVersion.Should().Be("v1.5.0");
        }
        finally { await CleanupLastAsync(); }
    }

    // ---- 12. Illegal Engine version blocked BEFORE the store write --------

    [Fact]
    public async Task Illegal_engine_version_is_blocked_before_store_write()
    {
        AnalysisRunOutcome outcome = await RunAsync(
            BuildResponse("9.9.9", UnknownStateContract.Unknown),
            "9.9.9",
            UnknownStateContract.Unknown);
        try
        {
            outcome.Succeeded.Should().BeFalse();
            outcome.Task.Should().NotBeNull();
            // Must land in OUTPUT_VALIDATION_FAILED (FAILED_VALIDATION), never RECOVERY_REQUIRED.
            outcome.Task!.Status.Should().Be(AnalysisTaskStatus.OutputValidationFailed);
            outcome.Task.Status.Should().NotBe(AnalysisTaskStatus.RecoveryRequired);

            // No analysis_results row was ever written (the gate intercepted before the commit chain).
            (await _lastStore!.GetAnalysisResultByTaskAsync(outcome.Task.TaskId)).Should().BeNull();
        }
        finally { await CleanupLastAsync(); }
    }

    // ---- 13. UnknownState still persists 'UNKNOWN' (M3-FIX-04 preserved) ---

    [Fact]
    public async Task UnknownState_still_persists_UNKOWN()
    {
        AnalysisRunOutcome outcome = await RunAsync(
            BuildResponse(EngineVersionContract.CanonicalVersion, UnknownStateContract.Unknown),
            EngineVersionContract.CanonicalVersion,
            UnknownStateContract.Unknown);
        try
        {
            string resultId = "RES-" + outcome.Task!.TaskId;
            AnalysisResult? result = await _lastStore!.GetAnalysisResultByTaskAsync(outcome.Task.TaskId);
            result.Should().NotBeNull();
            result!.UnknownState.Should().Be(UnknownStateContract.Unknown);
        }
        finally { await CleanupLastAsync(); }
    }

    // ---- 15. analysis_results persisted --------------------------------

    [Fact]
    public async Task analysis_results_persisted()
    {
        AnalysisRunOutcome outcome = await RunAsync(
            BuildResponse(EngineVersionContract.CanonicalVersion, UnknownStateContract.Unknown),
            EngineVersionContract.CanonicalVersion,
            UnknownStateContract.Unknown);
        try
        {
            AnalysisResult? result = await _lastStore!.GetAnalysisResultByTaskAsync(outcome.Task!.TaskId);
            result.Should().NotBeNull();
            result!.UnknownState.Should().Be(UnknownStateContract.Unknown);
            result.HardGate.Should().BeFalse();
        }
        finally { await CleanupLastAsync(); }
    }

    // ---- 16. runtime_snapshots persisted --------------------------------

    [Fact]
    public async Task runtime_snapshots_persisted()
    {
        AnalysisRunOutcome outcome = await RunAsync(
            BuildResponse(EngineVersionContract.CanonicalVersion, UnknownStateContract.Unknown),
            EngineVersionContract.CanonicalVersion,
            UnknownStateContract.Unknown);
        try
        {
            RuntimeSnapshot? snapshot = await _lastStore!.GetRuntimeSnapshotByResultAsync("RES-" + outcome.Task!.TaskId);
            snapshot.Should().NotBeNull();
        }
        finally { await CleanupLastAsync(); }
    }

    // ---- 17. evidence_bundles persisted ---------------------------------

    [Fact]
    public async Task evidence_bundles_persisted()
    {
        AnalysisRunOutcome outcome = await RunAsync(
            BuildResponse(EngineVersionContract.CanonicalVersion, UnknownStateContract.Unknown),
            EngineVersionContract.CanonicalVersion,
            UnknownStateContract.Unknown);
        try
        {
            EvidenceBundle? evidence = await _lastStore!.GetEvidenceBundleByResultAsync("RES-" + outcome.Task!.TaskId);
            evidence.Should().NotBeNull();
            evidence!.EvidenceDigest.Should().Be(new string('a', 64));
        }
        finally { await CleanupLastAsync(); }
    }

    // ---- 18. conflict_observations persisted by rule --------------------

    [Fact]
    public async Task conflict_observations_persisted_by_rule()
    {
        AnalysisRunOutcome outcome = await RunAsync(
            BuildResponse(EngineVersionContract.CanonicalVersion, UnknownStateContract.Unknown, withConflicts: true),
            EngineVersionContract.CanonicalVersion,
            UnknownStateContract.Unknown,
            withConflicts: true);
        try
        {
            List<ConflictObservation> conflicts = await _lastStore!.GetConflictObservationsByResultAsync("RES-" + outcome.Task!.TaskId);
            conflicts.Should().HaveCount(1);
            conflicts[0].ConflictType.Should().Be("KNOWLEDGE_GAP");
            conflicts[0].MissingContext.Should().NotBeNull();
            conflicts[0].MissingContext!.Value.Should().Contain("knowledge:v2");
            conflicts[0].HumanReviewRequired.Should().BeTrue();
        }
        finally { await CleanupLastAsync(); }
    }

    // ---- 19. Audit chain reaches AUDIT_COMMITTED ------------------------

    [Fact]
    public async Task Audit_chain_reaches_AUDIT_COMMITTED()
    {
        AnalysisRunOutcome outcome = await RunAsync(
            BuildResponse(EngineVersionContract.CanonicalVersion, UnknownStateContract.Unknown),
            EngineVersionContract.CanonicalVersion,
            UnknownStateContract.Unknown);
        try
        {
            List<AuditRecord> chain = await _lastStore!.GetAuditChainAsync(outcome.Task!.TaskId);
            var actions = chain.ConvertAll(a => a.Action);
            actions.Should().Contain("RESULT");
            actions.Should().Contain("AUDIT_COMMITTED");
            actions.Should().Contain("COMPLETED");
            actions.Should().NotContain("RECOVERY_REQUIRED");
            actions.Should().NotContain("COMMIT_FAILED");
        }
        finally { await CleanupLastAsync(); }
    }

    // ---- 20. Restart still shows the same (COMPLETED) Job ---------------

    [Fact]
    public async Task Restart_reopens_same_job_and_shows_COMPLETED()
    {
        (string dbPath, ObserverStore store, AnalysisWorkspace workspace) = await BuildWorkspaceAsync(
            BuildResponse(EngineVersionContract.CanonicalVersion, UnknownStateContract.Unknown));
        string taskId;
        try
        {
            var input = new RawAnalysisInput
            {
                Mode = "FORM",
                CanonicalInput = "{\"user_question\":\"q\",\"ai_output\":\"a\",\"context\":\"c\"}",
                ContentDigest = "digest-e2e-restart",
                TransformTrace = null,
            };
            AnalysisRunOutcome outcome = await workspace.CreateAndRunAsync(
                null, "SV-E2E", ImmutableArray<string>.Empty, input, RetentionMode.SanitizedPersistent);
            outcome.Succeeded.Should().BeTrue();
            taskId = outcome.Task!.TaskId;
        }
        finally
        {
            await store.DisposeAsync();
            SqliteConnection.ClearAllPools(); // keep the file to simulate a Host restart
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
            await reopened.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
