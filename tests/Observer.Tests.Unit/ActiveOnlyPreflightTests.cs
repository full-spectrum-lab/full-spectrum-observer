using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.Contracts.ReasonCodes;
using FullSpectrum.Observer.EngineFacade;
using FullSpectrum.Observer.Host.Web.Services;
using FullSpectrum.Observer.Store;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FullSpectrum.Observer.Tests.Unit;

/// <summary>
/// Active-only pre-flight behavior tests (plan §2). These exercise the REAL
/// <see cref="AnalysisWorkspace"/> orchestrator and the REAL <see cref="ObserverStore"/> against an
/// isolated temp SQLite database. An <see cref="IEngineFacade"/> spy (see <see cref="FakeEngineFacade"/>)
/// records whether the Engine was ever invoked — the core safety assertion for the Active-only gate.
///
/// <para>Scope: backend enforcement only (the authoritative safety boundary, plan §2.1 L3). The UI
/// filter (plan F6) is covered by an equivalent logic test but is explicitly NOT the security
/// boundary. No product source is modified; this file is additive.</para>
///
/// <para>F9 from the original draft plan was WITHDRAWN (plan §2.7): missing knowledge-version binding
/// is a registered product defect (MISSING_KNOWLEDGE_VERSION_PREFLIGHT) and is intentionally NOT
/// covered by a green test.</para>
/// </summary>
public sealed class ActiveOnlyPreflightTests
{
    // Hoisted array literals — CA1861 (enforced as error via TreatWarningsAsErrors) forbids repeated
    // inline `new[]` literals passed to a method; reference these cached fields instead.
    private static readonly string[] RetiredKnowledgeVersionIds = { "KSV-RET" };
    private static readonly string[] DraftKnowledgeVersionIds = { "KSV-DRAFT" };
    private static readonly string[] ActiveKnowledgeVersionIds = { "KSV-ACTIVE" };

    /// <summary>
    /// Engine spy: records whether <see cref="AnalyzeAsync"/> was called. On the Active-only rejection
    /// path it MUST stay false. Double-safety: when <c>failIfCalled</c> is set (or no canned response
    /// is supplied) the spy throws on the first call, so even a mis-written assertion cannot produce a
    /// silent PASS — the test goes red immediately.
    /// </summary>
    private sealed class FakeEngineFacade : IEngineFacade
    {
        private readonly EngineResponse? _response;
        private readonly bool _failIfCalled;

        public FakeEngineFacade(EngineResponse? response = null, bool failIfCalled = false)
        {
            _response = response;
            _failIfCalled = failIfCalled;
        }

        /// <summary>Whether the Engine was invoked. The core assertion object for the Active-only gate.</summary>
        public bool AnalyzeAsyncCalled { get; private set; }

        public Task<EngineResponse> AnalyzeAsync(EngineRequest request, CancellationToken cancellationToken = default)
        {
            AnalyzeAsyncCalled = true;
            if (_failIfCalled || _response is null)
            {
                throw new InvalidOperationException(
                    "ACTIVE_ONLY_VIOLATION: Engine invoked on a non-Active version scenario — the Active-only gate failed.");
            }

            return Task.FromResult(_response);
        }
    }

    /// <summary>Test fixture: isolated temp DB + real store/catalogs/workspace + the spy.</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        public string DbPath { get; init; } = string.Empty;
        public ObserverStore Store { get; init; } = null!;
        public SubjectCatalog Subjects { get; init; } = null!;
        public KnowledgeCatalog Knowledge { get; init; } = null!;
        public FakeEngineFacade Engine { get; init; } = null!;
        public AnalysisWorkspace Workspace { get; init; } = null!;

        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (File.Exists(DbPath))
            {
                File.Delete(DbPath);
            }
        }
    }

    // ---- F1: Draft subject version is rejected with PREFLIGHT_FAILED --------------------------------

    [Fact]
    public async Task Draft_subject_version_is_rejected_with_PREFLIGHT_FAILED()
    {
        Fixture fx = await BuildFixtureAsync(failIfCalled: true);
        try
        {
            await SeedSubjectAsync(fx.Store, "S-AO");
            await SeedSubjectVersionAsync(fx.Store, "S-AO", "SV-DRAFT", "Draft");

            AnalysisRunOutcome outcome = await fx.Workspace.CreateAndRunAsync(
                null, "SV-DRAFT", ImmutableArray<string>.Empty, BuildInput(), RetentionMode.SanitizedPersistent);

            await AssertPreflightRejectedAsync(fx.Store, outcome, fx.Engine);
        }
        finally
        {
            await fx.DisposeAsync();
        }
    }

    // ---- F2: Retired subject version is rejected with PREFLIGHT_FAILED ------------------------------

    [Fact]
    public async Task Retired_subject_version_is_rejected_with_PREFLIGHT_FAILED()
    {
        Fixture fx = await BuildFixtureAsync(failIfCalled: true);
        try
        {
            await SeedSubjectAsync(fx.Store, "S-AO");
            await SeedSubjectVersionAsync(fx.Store, "S-AO", "SV-RETIRED", "Retired");

            AnalysisRunOutcome outcome = await fx.Workspace.CreateAndRunAsync(
                null, "SV-RETIRED", ImmutableArray<string>.Empty, BuildInput(), RetentionMode.SanitizedPersistent);

            await AssertPreflightRejectedAsync(fx.Store, outcome, fx.Engine);
        }
        finally
        {
            await fx.DisposeAsync();
        }
    }

    // ---- F3: Active subject with Retired knowledge is rejected ----------------------------------------

    [Fact]
    public async Task Active_subject_with_Retired_knowledge_is_rejected()
    {
        Fixture fx = await BuildFixtureAsync(failIfCalled: true);
        try
        {
            await SeedSubjectAsync(fx.Store, "S-AO");
            await SeedSubjectVersionAsync(fx.Store, "S-AO", "SV-AO-ACTIVE", "Active");
            await SeedKnowledgeSourceAsync(fx.Store, "K-AO");
            await SeedKnowledgeVersionAsync(fx.Store, "K-AO", "KSV-RET", "Retired");

            AnalysisRunOutcome outcome = await fx.Workspace.CreateAndRunAsync(
                null, "SV-AO-ACTIVE", RetiredKnowledgeVersionIds, BuildInput(), RetentionMode.SanitizedPersistent);

            await AssertPreflightRejectedAsync(fx.Store, outcome, fx.Engine);
            // The rejection must be diagnosable: it names the offending knowledge version.
            outcome.ErrorMessage.Should().Contain("KSV-RET");
        }
        finally
        {
            await fx.DisposeAsync();
        }
    }

    // ---- F4: Active subject with Draft knowledge is rejected ------------------------------------------

    [Fact]
    public async Task Active_subject_with_Draft_knowledge_is_rejected()
    {
        Fixture fx = await BuildFixtureAsync(failIfCalled: true);
        try
        {
            await SeedSubjectAsync(fx.Store, "S-AO");
            await SeedSubjectVersionAsync(fx.Store, "S-AO", "SV-AO-ACTIVE", "Active");
            await SeedKnowledgeSourceAsync(fx.Store, "K-AO");
            await SeedKnowledgeVersionAsync(fx.Store, "K-AO", "KSV-DRAFT", "Draft");

            AnalysisRunOutcome outcome = await fx.Workspace.CreateAndRunAsync(
                null, "SV-AO-ACTIVE", DraftKnowledgeVersionIds, BuildInput(), RetentionMode.SanitizedPersistent);

            await AssertPreflightRejectedAsync(fx.Store, outcome, fx.Engine);
            outcome.ErrorMessage.Should().Contain("KSV-DRAFT");
        }
        finally
        {
            await fx.DisposeAsync();
        }
    }

    // ---- F5: a non-Draft task is NOT re-validated by the Active-only gate (no Recovery/Replay E2E) -----

    [Fact]
    public async Task Non_draft_task_is_not_revalidated_by_active_only_gate()
    {
        // A valid canned Engine response so the run can actually reach the Engine and complete.
        Fixture fx = await BuildFixtureAsync(BuildValidResponse(), failIfCalled: false);
        try
        {
            await SeedSubjectAsync(fx.Store, "S-AO");
            await SeedSubjectVersionAsync(fx.Store, "S-AO", "SV-AO-ACTIVE", "Active");

            // 1) Create a Draft task bound to an Active subject version.
            AnalysisTask task = await fx.Workspace.CreateDraftTaskAsync(
                "TASK-AO-F5", "SV-AO-ACTIVE", ImmutableArray<string>.Empty, BuildInput(), RetentionMode.SanitizedPersistent);

            // 2) Advance the task out of the Draft state (simulating precheck completion).
            await fx.Store.UpdateAnalysisTaskStatusAsync(task.TaskId, AnalysisTaskStatus.PrecheckPassed);

            // 3) Simulate the subject version being retired mid-flight.
            await MutateSubjectVersionStatusAsync(fx.DbPath, "SV-AO-ACTIVE", "Retired");

            // 4) Re-run. The Active-only gate only acts on Draft tasks, so this must proceed to the Engine.
            AnalysisRunOutcome outcome = await fx.Workspace.RunAnalysisAsync(task.TaskId);

            outcome.Succeeded.Should().BeTrue("a non-Draft task is not re-gated, so the run should reach COMPLETED");
            outcome.Task!.Status.Should().NotBe(AnalysisTaskStatus.PreflightFailed);
            fx.Engine.AnalyzeAsyncCalled.Should().BeTrue("the Engine MUST be invoked for non-Draft tasks");
        }
        finally
        {
            await fx.DisposeAsync();
        }
    }

    // ---- F6: catalog Active-filter selects only the Active version (UI filter logic equivalence) ------
    // This is an auxiliary LOGIC-EQUIVALENT assertion for the /new-analysis filter predicate
    // (v.Status == "Active"); it is NOT the backend security boundary (plan §2.1 L2) and is not part
    // of the L3 authoritative evidence set, but it counts toward the F1-F8 all-green requirement.

    [Fact]
    public async Task Catalog_active_filter_selects_only_active_versions()
    {
        Fixture fx = await BuildFixtureAsync(failIfCalled: true);
        try
        {
            const string subjectId = "S-AO-LOGIC";
            await SeedSubjectAsync(fx.Store, subjectId);
            await SeedSubjectVersionAsync(fx.Store, subjectId, "SV-AO-DRAFT", "Draft");
            await SeedSubjectVersionAsync(fx.Store, subjectId, "SV-AO-ACTIVE2", "Active");
            await SeedSubjectVersionAsync(fx.Store, subjectId, "SV-AO-RETIRED2", "Retired");

            List<SubjectVersion> all = await fx.Subjects.ListVersionsAsync(subjectId);
            all.Should().HaveCount(3, "all three seeded versions are persisted");

            // Same predicate the UI uses (NewAnalysis.razor OnInitializedAsync).
            List<SubjectVersion> activeOnly = all.Where(v => v.Status == "Active").ToList();
            activeOnly.Should().HaveCount(1, "only the Active version passes the filter");
            activeOnly[0].VersionId.Should().Be("SV-AO-ACTIVE2");
        }
        finally
        {
            await fx.DisposeAsync();
        }
    }

    // ---- F7: a rejected run produces no result / snapshot / evidence and never reaches COMPLETED ------

    [Fact]
    public async Task Rejected_run_produces_no_result_snapshot_or_evidence()
    {
        Fixture fx = await BuildFixtureAsync(failIfCalled: true);
        try
        {
            await SeedSubjectAsync(fx.Store, "S-AO");
            await SeedSubjectVersionAsync(fx.Store, "S-AO", "SV-DRAFT", "Draft");

            AnalysisRunOutcome outcome = await fx.Workspace.CreateAndRunAsync(
                null, "SV-DRAFT", ImmutableArray<string>.Empty, BuildInput(), RetentionMode.SanitizedPersistent);

            await AssertPreflightRejectedAsync(fx.Store, outcome, fx.Engine);

            string taskId = outcome.Task!.TaskId;
            (await fx.Store.GetAnalysisResultByTaskAsync(taskId)).Should().BeNull("no analysis result is written on rejection");
            (await fx.Store.GetRuntimeSnapshotByResultAsync("RES-" + taskId)).Should().BeNull("no runtime snapshot is written on rejection");
            (await fx.Store.GetEvidenceBundleByResultAsync("RES-" + taskId)).Should().BeNull("no evidence bundle is written on rejection");
            outcome.Task.Status.Should().NotBe(AnalysisTaskStatus.Completed, "a rejected run never reaches COMPLETED");
        }
        finally
        {
            await fx.DisposeAsync();
        }
    }

    // ---- F8: positive control — all Active bindings reach COMPLETED (rules out "every task fails") -----

    [Fact]
    public async Task All_active_versions_reach_COMPLETED_as_control()
    {
        Fixture fx = await BuildFixtureAsync(BuildValidResponse(), failIfCalled: false);
        try
        {
            await SeedSubjectAsync(fx.Store, "S-AO");
            await SeedSubjectVersionAsync(fx.Store, "S-AO", "SV-AO-ACTIVE", "Active");
            await SeedKnowledgeSourceAsync(fx.Store, "K-AO");
            await SeedKnowledgeVersionAsync(fx.Store, "K-AO", "KSV-ACTIVE", "Active");

            AnalysisRunOutcome outcome = await fx.Workspace.CreateAndRunAsync(
                null, "SV-AO-ACTIVE", ActiveKnowledgeVersionIds, BuildInput(), RetentionMode.SanitizedPersistent);

            outcome.Succeeded.Should().BeTrue("the positive control must complete");
            outcome.Task!.Status.Should().Be(AnalysisTaskStatus.Completed);
            (await fx.Store.GetAnalysisTaskAsync(outcome.Task.TaskId))!.Status.Should().Be(AnalysisTaskStatus.Completed);
            fx.Engine.AnalyzeAsyncCalled.Should().BeTrue("the Engine is invoked for valid Active bindings");
        }
        finally
        {
            await fx.DisposeAsync();
        }
    }

    // ---- Helpers ------------------------------------------------------------------------------------

    /// <summary>Asserts the common Active-only rejection contract: run fails, lands in PREFLIGHT_FAILED
    /// (verified both in-memory AND by re-reading the SQLite single source of truth), the diagnostic
    /// message names the Active-only rule, and the Engine was never invoked.</summary>
    private static async Task AssertPreflightRejectedAsync(ObserverStore store, AnalysisRunOutcome outcome, FakeEngineFacade engine)
    {
        outcome.Succeeded.Should().BeFalse();
        outcome.Task.Should().NotBeNull();
        outcome.Task!.Status.Should().Be(AnalysisTaskStatus.PreflightFailed);
        outcome.ErrorMessage.Should().Contain("仅 Active 版本可用于新分析");

        AnalysisTask reloaded = (await store.GetAnalysisTaskAsync(outcome.Task.TaskId))!;
        reloaded.Status.Should().Be(AnalysisTaskStatus.PreflightFailed, "the authoritative status must match the SQLite single source of truth");

        engine.AnalyzeAsyncCalled.Should().BeFalse("the Engine must NOT be called on an Active-only rejection");
    }

    private static async Task<Fixture> BuildFixtureAsync(EngineResponse? response = null, bool failIfCalled = false)
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"fsp-active-only-{Guid.NewGuid():N}.db");
        if (File.Exists(dbPath))
        {
            File.Delete(dbPath);
        }

        var store = new ObserverStore(dbPath);
        await store.EnsureSchemaAsync();

        var auditContext = new AuditContext();
        var auditViewer = new AuditViewer(store, auditContext);
        var subjects = new SubjectCatalog(store, auditContext, auditViewer);
        var knowledge = new KnowledgeCatalog(store, auditContext, auditViewer);
        var shutdown = new AnalysisShutdownToken();
        var intake = new IntakeAdapter();
        var output = new OutputAdapter();
        var engine = new FakeEngineFacade(response, failIfCalled);
        var workspace = new AnalysisWorkspace(store, engine, intake, output, auditViewer, subjects, knowledge, shutdown);

        return new Fixture
        {
            DbPath = dbPath,
            Store = store,
            Subjects = subjects,
            Knowledge = knowledge,
            Engine = engine,
            Workspace = workspace,
        };
    }

    private static async Task SeedSubjectAsync(ObserverStore store, string subjectId)
    {
        await store.InsertSubjectAsync(new ObservedSubject
        {
            LocalSubjectId = subjectId,
            SubjectType = "PERSON",
            Mode = "OBSERVE",
            ConcentrationTier = null,
            CreatedAt = DateTime.UtcNow.ToString("O"),
        });
    }

    private static async Task SeedSubjectVersionAsync(ObserverStore store, string subjectId, string versionId, string status)
    {
        string now = DateTime.UtcNow.ToString("O");
        await store.InsertSubjectVersionAsync(new SubjectVersion
        {
            VersionId = versionId,
            SubjectId = subjectId,
            Status = status,
            Seq = 1,
            Payload = "{}",
            SchemaVersion = "1.0.0",
            CreatedAt = now,
            ActiveFrom = status == "Active" ? now : null,
            RetiredAt = status == "Retired" ? now : null,
        });
    }

    private static async Task SeedKnowledgeSourceAsync(ObserverStore store, string sourceId)
    {
        await store.InsertKnowledgeSourceAsync(new KnowledgeSource
        {
            SourceId = sourceId,
            LibraryId = "LIB",
            Name = "n",
            CreatedAt = DateTime.UtcNow.ToString("O"),
        });
    }

    private static async Task SeedKnowledgeVersionAsync(ObserverStore store, string sourceId, string versionId, string status)
    {
        string now = DateTime.UtcNow.ToString("O");
        await store.InsertKnowledgeSourceVersionAsync(new KnowledgeSourceVersion
        {
            VersionId = versionId,
            SourceId = sourceId,
            Digest = new string('a', 64),
            Applicability = "ANY",
            Status = status,
            Seq = 1,
            Payload = "{}",
            CreatedAt = now,
            EffectiveTime = status == "Active" ? now : null,
        });
    }

    /// <summary>Directly mutates a subject version's status in the DB (versions are otherwise immutable
    /// via the catalog API). Used by F5 to simulate a version being retired mid-flight.</summary>
    private static async Task MutateSubjectVersionStatusAsync(string dbPath, string versionId, string status)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE subject_versions SET status = @status WHERE version_id = @vid";
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@vid", versionId);
        await command.ExecuteNonQueryAsync();
    }

    private static RawAnalysisInput BuildInput() => new()
    {
        Mode = "FORM",
        CanonicalInput = "{\"user_question\":\"q\",\"ai_output\":\"a\",\"context\":\"c\"}",
        ContentDigest = "digest-ao-" + Guid.NewGuid().ToString("N"),
        TransformTrace = null,
    };

    /// <summary>Builds a contract-valid v1.5.0 Engine response (mirrors the E2E template) so a run that
    /// passes the Active-only gate can reach COMPLETED.</summary>
    private static EngineResponse BuildValidResponse() => new()
    {
        EngineVersion = EngineVersionContract.CanonicalVersion,
        EngineCommit = EngineV15Contract.EngineCommit,
        SchemaVersion = "1.0.0",
        SchemaDigest = new string('a', 64),
        AnalyzerVersion = EngineV15Contract.AnalyzerVersion,
        ProfileVersion = EngineV15Contract.ProfileVersion,
        Conclusion = JsonSerializer.SerializeToElement(new { result = "ok", score = 0.42 }),
        ConflictObservations = new List<EngineConflictObservation>(),
        UnknownState = UnknownStateContract.Unknown,
        HardGate = false,
        RuntimeDigest = new string('a', 64),
        ReplayRef = new EngineReplayRef { Digest = new string('a', 64), EngineVersion = EngineVersionContract.CanonicalVersion },
        Evidence = new EngineEvidence { EvidenceDigest = new string('a', 64), References = new List<string> { "CER/rv1" } },
    };
}
