using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using FullSpectrum.Observer.Contracts;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.Contracts.Serialization;
using FullSpectrum.Observer.EngineFacade;
using FullSpectrum.Observer.Execution.ScenarioPacks;
using FullSpectrum.Observer.Store;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FullSpectrum.Observer.Tests.Integration;

public sealed class ScenarioPackRealEngineTests
{
    [Fact]
    public async Task Two_synthetic_packs_execute_through_the_same_real_Engine_v1_5_path()
    {
        string root = RepositoryLayout.FindRoot();
        var loader = new ScenarioPackLoader(
            Path.Combine(root, "schemas", "scenario-pack", "v1", "scenario-pack-manifest.schema.json"),
            Path.Combine(root, "config", "scenario-pack-trust-roots.json"),
            "v0.4.0-beta",
            "v1.5.0");
        LoadedScenarioPack knowledge = loader.Load(
            Path.Combine(root, "packs", "scenario-packs", "knowledge-conflict", "1.0.0"));
        LoadedScenarioPack policy = loader.Load(
            Path.Combine(root, "packs", "scenario-packs", "synthetic-policy-conflict", "1.0.0"));
        var factory = new ScenarioPackEngineRequestFactory();
        ScenarioPackEngineRequest knowledgeRequest = factory.BuildGoldenRequest(knowledge, "KC_GOLDEN_001");
        ScenarioPackEngineRequest boundaryRequest = factory.BuildGoldenRequest(knowledge, "KC_BOUNDARY_001");
        ScenarioPackEngineRequest adversarialRequest = factory.BuildGoldenRequest(knowledge, "KC_ADVERSARIAL_001");
        ScenarioPackEngineRequest policyRequest = factory.BuildGoldenRequest(policy, "SPC_GOLDEN_001");
        var engine = new FullSpectrum.Observer.EngineFacade.EngineFacade(new EngineFacadeOptions
        {
            PythonExecutablePath = RequirePrivatePython(),
            WorkerScriptPath = Path.Combine(root, "engine", "worker", "worker.py"),
            EngineRootPath = Path.Combine(root, "engine", "vendor", "full-spectrum-engine"),
            WorkerLockPath = Path.Combine(root, "engine", "worker.lock.json"),
            SchemaDirectory = RepositoryLayout.SchemaDirectory(root),
            DefaultTimeout = TimeSpan.FromSeconds(60),
        });

        EngineResponse knowledgeFirst = await engine.AnalyzeAsync(knowledgeRequest.Request);
        EngineResponse knowledgeReplay = await engine.AnalyzeAsync(knowledgeRequest.Request);
        EngineResponse boundaryResponse = await engine.AnalyzeAsync(boundaryRequest.Request);
        EngineResponse adversarialResponse = await engine.AnalyzeAsync(adversarialRequest.Request);
        EngineResponse policyFirst = await engine.AnalyzeAsync(policyRequest.Request);

        knowledgeFirst.EngineVersion.Should().Be("v1.5.0");
        policyFirst.EngineVersion.Should().Be("v1.5.0");
        knowledgeFirst.RuntimeDigest.Should().HaveLength(64);
        policyFirst.RuntimeDigest.Should().HaveLength(64);
        knowledgeReplay.RuntimeDigest.Should().Be(knowledgeFirst.RuntimeDigest);
        knowledgeReplay.Conclusion?.GetRawText().Should().Be(knowledgeFirst.Conclusion?.GetRawText());
        policyFirst.ResolvedSimulationId.Should().NotBe(knowledgeFirst.ResolvedSimulationId);
        knowledgeFirst.UnknownState.Should().Be("UNKNOWN");
        policyFirst.UnknownState.Should().Be("UNKNOWN");
        knowledgeFirst.HardGate.Should().BeFalse();
        policyFirst.HardGate.Should().BeFalse();
        boundaryResponse.UnknownState.Should().Be("UNKNOWN");
        adversarialResponse.UnknownState.Should().Be("UNKNOWN");
        boundaryResponse.HardGate.Should().BeFalse();
        adversarialResponse.HardGate.Should().BeFalse();
        adversarialResponse.Conclusion?.GetRawText().Should().NotContain("authorize export immediately");

        string dbPath = Path.Combine(Path.GetTempPath(), $"observer-v04-engine-{Guid.NewGuid():N}.db");
        var store = new ObserverStore(dbPath);
        try
        {
            await store.EnsureSchemaAsync();
            await store.InstallScenarioPackAsync(new ScenarioPackRegistration
            {
                PackId = knowledge.Identity.PackId, Version = knowledge.Identity.Version, Digest = knowledge.Identity.Digest,
                ManifestJson = JsonSerializer.Serialize(knowledge.Manifest, FoundationJson.CreateOptions()),
                InstalledAtUtc = "2026-08-25T00:00:00.0000000Z",
            });
            string batchInput = File.ReadAllText(Path.Combine(
                knowledge.RootDirectory, "golden", "golden-001.input.json"), Encoding.UTF8);
            ScenarioPackBatchDraft batchDraft = ScenarioPackBatchFactory.Create(
                knowledge, "BATCH-V04-REAL-ENGINE", "IMPORT-V04-REAL-ENGINE",
                [("ITEM-V04-REAL-ENGINE", batchInput)],
                "authorization:synthetic-test", "redaction:synthetic-clean", "deletion:test-cleanup",
                "2026-08-25T00:00:00.0000000Z");
            await store.CreateScenarioPackBatchAsync(batchDraft.Batch, batchDraft.Items);
            ScenarioPackBatchItem claimed = (await store.ClaimNextScenarioPackBatchItemAsync(
                batchDraft.Batch.BatchId, "2026-08-25T00:00:00.1000000Z"))!;
            ScenarioPackEngineRequest batchRequest = factory.BuildRequest(
                knowledge, claimed.ItemId, Encoding.UTF8.GetBytes(claimed.InputJson));
            EngineResponse batchResponse = await engine.AnalyzeAsync(batchRequest.Request);
            await store.CompleteScenarioPackBatchItemAsync(
                batchDraft.Batch.BatchId, claimed.ItemId, batchResponse.RuntimeDigest,
                "2026-08-25T00:00:00.9000000Z");
            await SeedTaskAsync(store, batchRequest);
            await store.BindScenarioPackToTaskAsync(new ScenarioPackTaskBinding
            {
                TaskId = "TASK-V04-REAL-ENGINE", PackId = knowledge.Identity.PackId,
                PackVersion = knowledge.Identity.Version, PackDigest = knowledge.Identity.Digest,
                ProfileRefsJson = ScenarioPackEngineRequestFactory.CreateProfileBindingJson(batchRequest),
                BoundAtUtc = "2026-08-25T00:00:00.0000000Z",
            });
            ScenarioPackExecutionProjection projection = ScenarioPackEngineResultProjector.Project(
                "TASK-V04-REAL-ENGINE", "RESULT-V04-REAL-ENGINE", batchRequest, batchResponse,
                "2026-08-25T00:00:01.0000000Z");
            await store.InsertAnalysisResultAsync(projection.Result);
            await store.InsertRuntimeSnapshotAsync(projection.Snapshot);
            if (projection.Evidence is not null) await store.InsertEvidenceBundleAsync(projection.Evidence);
            await store.InsertConflictObservationsAsync(projection.Observations);

            (await store.GetAnalysisResultByTaskAsync("TASK-V04-REAL-ENGINE")).Should().Be(projection.Result);
            (await store.GetRuntimeSnapshotByResultAsync(projection.Result.ResultId))!.ConfigDigest
                .Should().Be(projection.Snapshot.ConfigDigest);
            (await store.GetScenarioPackTaskBindingAsync("TASK-V04-REAL-ENGINE"))!.ProfileRefsJson
                .Should().Contain(batchRequest.Profiles.Single().Digest);
            ScenarioPackBatch storedBatch = (await store.GetScenarioPackBatchAsync(batchDraft.Batch.BatchId))!;
            storedBatch.Status.Should().Be(ScenarioPackBatchStatus.Completed);
            storedBatch.CompletedItems.Should().Be(1);
            storedBatch.FailedItems.Should().Be(0);
        }
        finally
        {
            await store.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    private static async Task SeedTaskAsync(ObserverStore store, ScenarioPackEngineRequest request)
    {
        const string now = "2026-08-25T00:00:00.0000000Z";
        await store.InsertSubjectAsync(new ObservedSubject
        {
            LocalSubjectId = "SUBJ-V04-REAL-ENGINE", SubjectType = "SYNTHETIC", Mode = "V04_TEST", CreatedAt = now,
        });
        await store.InsertSubjectVersionAsync(new SubjectVersion
        {
            VersionId = "SUBV-V04-REAL-ENGINE", SubjectId = "SUBJ-V04-REAL-ENGINE", Status = "Active", Seq = 1,
            Payload = "{\"subject_type\":\"SYNTHETIC\",\"mode\":\"V04_TEST\"}", SchemaVersion = "test/1",
            CreatedAt = now, ActiveFrom = now,
        });
        await store.InsertAnalysisTaskAsync(AnalysisTask.Create(
            "TASK-V04-REAL-ENGINE", "SUBV-V04-REAL-ENGINE", ImmutableArray<string>.Empty,
            new RawAnalysisInput
            {
                Mode = "JSON_IMPORT", CanonicalInput = request.Request.Input.CanonicalInput.GetRawText(),
                ContentDigest = request.InputDigest, TransformTrace = request.Request.Input.TransformTrace.GetRawText(),
            },
            "SANITIZED_PERSISTENT", now));
    }

    private static string RequirePrivatePython()
    {
        string value = Environment.GetEnvironmentVariable("FSP_PRIVATE_PYTHON") ?? string.Empty;
        if (!Path.IsPathFullyQualified(value) || !File.Exists(value))
        {
            throw new FileNotFoundException("FSP_PRIVATE_PYTHON must identify the pinned private Python runtime.", value);
        }
        return value;
    }
}
