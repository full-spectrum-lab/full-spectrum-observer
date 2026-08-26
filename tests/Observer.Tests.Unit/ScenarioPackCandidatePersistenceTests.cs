using System.Collections.Immutable;
using FluentAssertions;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.Store;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FullSpectrum.Observer.Tests.Unit;

public sealed class ScenarioPackCandidatePersistenceTests
{
    [Fact]
    public async Task Candidate_ledger_is_round_trip_and_append_only()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"observer-v04-candidate-{Guid.NewGuid():N}.db");
        await using var store = new ObserverStore(dbPath);
        try
        {
            await store.EnsureSchemaAsync();
            await SeedResultAsync(store);
            var candidate = new ScenarioPackCandidateObservation
            {
                CandidateId = "CAND-1",
                ResultId = "RESULT-1",
                SampleId = "RP-001",
                AssertionKey = "pilot_log_is_owner_real_evolution",
                SourceRefs = ["ai-first@1", "owner-correction@1"],
                ClaimDigests = [new string('a', 64), new string('b', 64)],
                MinorityEvidenceRefs = ["owner-correction@1"],
                MissingContext = ImmutableArray<string>.Empty,
                ReasonCode = "KC_STRUCTURED_OPPOSITION_CANDIDATE",
                AdapterVersion = "KC-STRUCTURED-OPPOSITION-1",
                SignatureKeyId = "owner-rp001",
                TrustStoreDigest = new string('c', 64),
                HumanReviewRequired = true,
                AuthorizedAction = false,
                CandidateDigest = new string('d', 64),
                CreatedAtUtc = "2026-08-27T00:00:00Z",
            };

            await store.InsertScenarioPackCandidateObservationsAsync([candidate]);
            (await store.GetScenarioPackCandidateObservationsAsync("RESULT-1"))
                .Should().BeEquivalentTo([candidate], options => options.WithStrictOrdering());

            await using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=false;");
            await connection.OpenAsync();
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE scenario_pack_candidate_observations SET sample_id='changed' WHERE candidate_id='CAND-1'";
            Func<Task> mutate = async () => { _ = await update.ExecuteNonQueryAsync(); };
            await mutate.Should().ThrowAsync<SqliteException>()
                .WithMessage("*SCENARIO_PACK_CANDIDATE_IMMUTABLE*");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    private static async Task SeedResultAsync(ObserverStore store)
    {
        const string now = "2026-08-27T00:00:00Z";
        await store.InsertSubjectAsync(new ObservedSubject
        {
            LocalSubjectId = "SUBJ-1", SubjectType = "KnowledgeSource", Mode = "V04_TEST", CreatedAt = now,
        });
        await store.InsertSubjectVersionAsync(new SubjectVersion
        {
            VersionId = "SUBV-1", SubjectId = "SUBJ-1", Status = "Active", Seq = 1,
            Payload = "{}", SchemaVersion = "test/1", CreatedAt = now, ActiveFrom = now,
        });
        await store.InsertAnalysisTaskAsync(AnalysisTask.Create(
            "TASK-1", "SUBV-1", [], new RawAnalysisInput
            {
                Mode = "JSON_IMPORT", CanonicalInput = "{}", ContentDigest = new string('e', 64), TransformTrace = "[]",
            }, "SANITIZED_PERSISTENT", now));
        await store.InsertAnalysisResultAsync(new AnalysisResult
        {
            ResultId = "RESULT-1", TaskId = "TASK-1", ConclusionPayload = "{}",
            UnknownState = "UNKNOWN", HardGate = false, CreatedAt = now,
        });
    }
}
