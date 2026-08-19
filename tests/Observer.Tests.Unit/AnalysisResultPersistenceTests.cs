using System.Collections.Immutable;
using System.Threading.Tasks;
using FluentAssertions;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.Host.Web.Services;
using FullSpectrum.Observer.Store;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FullSpectrum.Observer.Tests.Unit;

/// <summary>
/// F4 red-line guard: the DB storage layer and the read-only presenter must persist / expose the
/// Engine conclusion EXACTLY as received (byte-equal to <c>ConclusionPayload</c>). No recompute,
/// no rewrite, no HTML/JSON re-serialization may alter it.
///
/// NOTE: uses a named in-memory SQLite database (Mode=Memory;Cache=Shared) instead of a temp file.
/// The sandbox file watcher intermittently locks *.db files under %TEMP%, which makes file-based
/// round-trips flaky; an in-memory store removes that environmental failure mode entirely while
/// still exercising the real ObserverStore persistence path. Because ObserverStore opens a fresh
/// connection per operation, we keep one holder connection open for the test duration so the
/// shared in-memory cache (Cache=Shared) survives across those operations.
/// </summary>
public sealed class AnalysisResultPersistenceTests
{
    private static string NewInMemoryDb() => "obs-persist-" + System.Guid.NewGuid().ToString("N") + ";Mode=Memory;Cache=Shared";

    /// <summary>
    /// Seeds the minimal valid FK chain required before an <c>analysis_results</c> row can be
    /// inserted (the schema enforces: subjects &lt;- subject_versions &lt;- analysis_tasks &lt;-
    /// analysis_results). The rows are synthetic fixtures for this red-line test only; their
    /// content is irrelevant to the assertion, which concerns the child conclusion payload.
    /// </summary>
    private static async Task SeedParentsAsync(ObserverStore store, string taskId)
    {
        string subjectId = "SUBJ-" + taskId;
        string versionId = "SUBV-" + taskId;

        await store.InsertSubjectAsync(new ObservedSubject
        {
            LocalSubjectId = subjectId,
            SubjectType = "AI_AGENT",
            Mode = "CROSS_BORDER_PAYMENT",
            CreatedAt = "2026-01-01T00:00:00Z",
        });

        await store.InsertSubjectVersionAsync(new SubjectVersion
        {
            VersionId = versionId,
            SubjectId = subjectId,
            Status = "Draft",
            Seq = 1,
            Payload = "{\"subject_type\":\"AI_AGENT\",\"mode\":\"CROSS_BORDER_PAYMENT\"}",
            SchemaVersion = "v1",
            CreatedAt = "2026-01-01T00:00:00Z",
        });

        await store.InsertAnalysisTaskAsync(new AnalysisTask
        {
            TaskId = taskId,
            SubjectVersionId = versionId,
            KnowledgeVersionIds = ImmutableArray<string>.Empty,
            InputMode = "FORM",
            CanonicalInput = "{}",
            ContentDigest = "deadbeef",
            RetentionMode = "FULL_LOCAL",
            Status = "COMPLETED",
            CreatedAt = "2026-01-01T00:00:00Z",
        });
    }

    private static async Task RoundTripAsync(string original, string resultId, string taskId, string unknownState, bool hardGate)
    {
        string dbPath = NewInMemoryDb();
        // Install the patched e_sqlite3 provider before any connection is opened. The store does
        // this in its static constructor, but the holder is a raw Microsoft.Data.Sqlite connection
        // opened before `new ObserverStore(...)`; in an isolated run no other store type has
        // triggered the static ctor yet, so we initialize explicitly (idempotent, production-faithful).
        SqliteRuntimeBootstrap.Initialize();
        // Holder connection: keeps the shared in-memory cache alive across ObserverStore's
        // per-operation connections (same connection string => same Cache=Shared database).
        await using var holder = new SqliteConnection("Data Source=" + dbPath + ";Pooling=true;");
        await holder.OpenAsync();

        await using var store = new ObserverStore(dbPath);
        await store.EnsureSchemaAsync();
        // Seed the minimal valid parent chain so the analysis_results FK constraint is satisfied.
        await SeedParentsAsync(store, taskId);

        var result = new AnalysisResult
        {
            ResultId = resultId,
            TaskId = taskId,
            ConclusionPayload = original,
            UnknownState = unknownState,
            HardGate = hardGate,
            CreatedAt = "2026-01-01T00:00:00Z",
        };
        await store.InsertAnalysisResultAsync(result);

        AnalysisResult? back = await store.GetAnalysisResultByTaskAsync(taskId);
        back.Should().NotBeNull();
        back!.ConclusionPayload.Should().Be(original);                 // byte-equal
        back.ConclusionPayload.Should().Be(result.ConclusionPayload);

        // In-memory equivalent of "write to store unchanged": the read-only presenter must not
        // mutate the original either.
        var model = GovernanceResultPresenter.PresentConclusion(original);
        model.VerbatimPayload.Should().Be(original);
        model.ReadableText.Should().Be(original);
    }

    // DB round-trip: inserted ConclusionPayload == read-back ConclusionPayload (byte-equal).
    [Fact]
    public async Task ConclusionPayload_persisted_verbatim_round_trip()
    {
        const string original = "{\"conclusion\":\"UNKNOWN <x> & \\\"q\\\" \\n tab\",\"score\":0.42}";
        await RoundTripAsync(original, "RES-P-1", "T-P-1", "UNKNOWN", false);
    }

    // The DB layer never appends, truncates, or re-encodes the conclusion.
    [Fact]
    public async Task ConclusionPayload_with_special_chars_survives_round_trip()
    {
        const string original = "结论含特殊字符：<b> & ' \" \\n \\t 中文";
        await RoundTripAsync(original, "RES-P-2", "T-P-2", "PARTIAL", true);
    }
}
