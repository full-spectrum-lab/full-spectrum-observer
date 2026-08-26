using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FullSpectrum.Observer.Contracts;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.Contracts.Serialization;
using FullSpectrum.Observer.EngineFacade;
using FullSpectrum.Observer.Execution.ScenarioPacks;
using FullSpectrum.Observer.Store;

namespace FullSpectrum.Observer.Host.Cli;

/// <summary>
/// Minimal local v0.4 entry point. It runs one signed Scenario Pack golden case through the real
/// Engine v1.5 path and persists the candidate-only evidence chain. It intentionally has no remote
/// listener, identity/RBAC layer, production action, or target-user pilot claims.
/// </summary>
public static class ScenarioPackCommand
{
    private static readonly JsonSerializerOptions IndentedJsonOptions = CreateIndentedOptions();
    public static async Task<int> RunAsync(
        CliOptions options,
        string dataDirectory,
        CancellationToken cancellationToken)
    {
        string packPath = Path.GetFullPath(options.Require("--pack"));
        string? requestedCase = options.Get("--case");
        string authorizationRef = options.Require("--authorization-ref");
        string redactionRef = options.Require("--redaction-ref");
        string deletionRef = options.Require("--deletion-ref");
        RuntimeConfigurationResolver.RuntimeConfiguration config = RuntimeConfigurationResolver.Resolve();
        string schemaPath = Path.Combine(config.PackageRoot, "schemas", "scenario-pack", "v1", "scenario-pack-manifest.schema.json");
        string trustRootPath = Path.Combine(config.PackageRoot, "config", "scenario-pack-trust-roots.json");
        var loader = new ScenarioPackLoader(schemaPath, trustRootPath, "v0.4.0-beta", "v1.5.0");
        LoadedScenarioPack pack = loader.Load(packPath);
        ScenarioPackGoldenCase golden = pack.Manifest.GoldenCases.SingleOrDefault(item =>
            requestedCase is null || string.Equals(item.CaseId, requestedCase, StringComparison.Ordinal))
            ?? throw new ArgumentException("The requested --case is not declared by the Scenario Pack.");

        string now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        string caseToken = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{pack.Identity.PackId}|{pack.Identity.Version}|{pack.Identity.Digest}|{golden.CaseId}")))[..16];
        string batchId = options.Get("--batch-id") ?? $"BATCH-V04-{caseToken}";
        string idempotencyKey = options.Get("--idempotency-key") ?? batchId;
        string itemId = $"ITEM-V04-{caseToken}";
        string taskId = $"TASK-V04-{caseToken}";

        SqliteRuntimeBootstrap.Initialize();
        string dbPath = Path.Combine(Path.GetFullPath(dataDirectory), "observer_console.db");
        await using var store = new ObserverStore(dbPath);
        await store.EnsureSchemaAsync();
        await store.InstallScenarioPackAsync(new ScenarioPackRegistration
        {
            PackId = pack.Identity.PackId,
            Version = pack.Identity.Version,
            Digest = pack.Identity.Digest,
            ManifestJson = File.ReadAllText(Path.Combine(pack.RootDirectory, "scenario-pack.manifest.json"), Encoding.UTF8),
            InstalledAtUtc = now,
        });
        await AppendAuditAsync(store, ScenarioPackAuditEventType.PackInstalled,
            pack.Identity.PackId + "@" + pack.Identity.Version, pack.Identity.Digest, null);

        string inputPath = ResolveContained(pack.RootDirectory, golden.InputRef);
        string inputJson = File.ReadAllText(inputPath, Encoding.UTF8);
        ScenarioPackBatchDraft draft = ScenarioPackBatchFactory.Create(
            pack,
            batchId,
            idempotencyKey,
            [(itemId, inputJson)],
            authorizationRef,
            redactionRef,
            deletionRef,
            now);
        ScenarioPackBatch batch = await store.CreateScenarioPackBatchAsync(draft.Batch, draft.Items);
        await AppendAuditAsync(store, ScenarioPackAuditEventType.BatchImported,
            batch.BatchId, batch.InputDigest, null);
        ScenarioPackBatchItem? claimed = await store.ClaimNextScenarioPackBatchItemAsync(batch.BatchId, now);
        if (claimed is null)
        {
            Write(new
            {
                status = batch.Status,
                idempotent = true,
                batch_id = batch.BatchId,
                pack_id = pack.Identity.PackId,
                pack_version = pack.Identity.Version,
                pack_digest = pack.Identity.Digest,
            }, options.Has("--json"));
            return 0;
        }

        ScenarioPackEngineRequest request = new ScenarioPackEngineRequestFactory().BuildRequest(
            pack, claimed.ItemId, Encoding.UTF8.GetBytes(claimed.InputJson));
        await SeedTaskAsync(store, request, taskId, now);
        await store.BindScenarioPackToTaskAsync(new ScenarioPackTaskBinding
        {
            TaskId = taskId,
            PackId = pack.Identity.PackId,
            PackVersion = pack.Identity.Version,
            PackDigest = pack.Identity.Digest,
            ProfileRefsJson = ScenarioPackEngineRequestFactory.CreateProfileBindingJson(request),
            BoundAtUtc = now,
        });
        await AppendAuditAsync(store, ScenarioPackAuditEventType.TaskBound,
            taskId, request.InputDigest, taskId);

        try
        {
            EngineFacadeOptions engineOptions = new()
            {
                PythonExecutablePath = config.PythonExecutablePath,
                WorkerScriptPath = config.WorkerScriptPath,
                EngineRootPath = config.EngineRootPath,
                WorkerLockPath = config.WorkerLockPath,
                SchemaDirectory = config.SchemaDirectory,
                DefaultTimeout = TimeSpan.FromSeconds(options.GetInt("--timeout", 60)),
            };
            IEngineFacade engine = new FullSpectrum.Observer.EngineFacade.EngineFacade(engineOptions);
            EngineResponse response = await engine.AnalyzeAsync(request.Request, cancellationToken);
            ScenarioPackExecutionProjection projection = ScenarioPackEngineResultProjector.Project(
                taskId,
                $"RESULT-{caseToken}",
                request,
                response,
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await store.UpdateAnalysisTaskStatusAsync(taskId, AnalysisTaskStatus.PrecheckPassed);
            await store.UpdateAnalysisTaskStatusAsync(taskId, AnalysisTaskStatus.EngineCompleted);
            await store.UpdateAnalysisTaskStatusAsync(taskId, AnalysisTaskStatus.OutputValidated);
            await store.InsertAnalysisResultAsync(projection.Result);
            await store.InsertRuntimeSnapshotAsync(projection.Snapshot);
            if (projection.Evidence is not null)
                await store.InsertEvidenceBundleAsync(projection.Evidence);
            await store.InsertConflictObservationsAsync(projection.Observations);
            await store.UpdateAnalysisTaskStatusAsync(taskId, AnalysisTaskStatus.Completed);
            await store.CompleteScenarioPackBatchItemAsync(batch.BatchId, claimed.ItemId, projection.Snapshot.RuntimeDigest, now);
            await AppendAuditAsync(store, ScenarioPackAuditEventType.ResultPersisted,
                projection.Result.ResultId, projection.Snapshot.RuntimeDigest, taskId);
            Write(new
            {
                status = "COMPLETED",
                candidate_only = true,
                human_review_required = true,
                authorized_action = false,
                batch_id = batch.BatchId,
                task_id = taskId,
                result_id = projection.Result.ResultId,
                pack_id = pack.Identity.PackId,
                pack_version = pack.Identity.Version,
                pack_digest = pack.Identity.Digest,
                unknown_state = projection.Result.UnknownState,
                runtime_digest = projection.Snapshot.RuntimeDigest,
            }, options.Has("--json"));
            return 0;
        }
        catch (Exception exception)
        {
            string errorCode = exception is InvalidDataException dataException &&
                                dataException.Message.StartsWith("SCENARIO_PACK_", StringComparison.Ordinal)
                ? dataException.Message.Split(':', 2)[0]
                : "SCENARIO_PACK_RUN_FAILED";
            await store.FailScenarioPackBatchItemAsync(batch.BatchId, claimed.ItemId, errorCode, now);
            Write(new
            {
                status = "FAILED",
                candidate_only = true,
                batch_id = batch.BatchId,
                task_id = taskId,
                error_code = errorCode,
            }, options.Has("--json"));
            return 70;
        }
    }

    private static async Task SeedTaskAsync(
        ObserverStore store,
        ScenarioPackEngineRequest request,
        string taskId,
        string now)
    {
        string subjectId = $"SUBJ-{taskId[9..]}";
        string subjectVersionId = $"SUBV-{taskId[9..]}";
        await store.InsertSubjectAsync(new ObservedSubject
        {
            LocalSubjectId = subjectId,
            SubjectType = request.Request.Subject.SubjectType,
            Mode = "V04_LOCAL_RUNNER",
            CreatedAt = now,
        });
        await store.InsertSubjectVersionAsync(new SubjectVersion
        {
            VersionId = subjectVersionId,
            SubjectId = subjectId,
            Status = "Active",
            Seq = 1,
            Payload = $"{{\"subject_type\":\"{request.Request.Subject.SubjectType}\",\"mode\":\"V04_LOCAL_RUNNER\"}}",
            SchemaVersion = request.Request.SchemaVersion,
            CreatedAt = now,
            ActiveFrom = now,
        });
        RawAnalysisInput input = new()
        {
            Mode = request.Request.Input.Mode,
            CanonicalInput = request.Request.Input.CanonicalInput.GetRawText(),
            ContentDigest = request.InputDigest,
            TransformTrace = request.Request.Input.TransformTrace.GetRawText(),
        };
        await store.InsertAnalysisTaskAsync(AnalysisTask.Create(
            taskId,
            subjectVersionId,
            [],
            input,
            RetentionMode.SanitizedPersistent.ToWire(),
            now));
    }

    private static string ResolveContained(string root, string reference)
    {
        string candidate = Path.GetFullPath(Path.Combine(root, reference.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate))
            throw new FileNotFoundException($"Scenario Pack input is unavailable: {reference}");
        return candidate;
    }

    private static async Task AppendAuditAsync(
        ObserverStore store,
        string eventType,
        string entityRef,
        string entityDigest,
        string? taskId)
    {
        AuditRecord? previous = await store.GetLatestAuditAsync();
        DateTimeOffset at = DateTimeOffset.UtcNow;
        if (previous is not null &&
            DateTimeOffset.TryParse(previous.At, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset previousAt) &&
            at <= previousAt)
        {
            at = previousAt.AddTicks(1);
        }
        string atUtc = at.ToString("O", CultureInfo.InvariantCulture);
        ScenarioPackAuditAppend append = ScenarioPackAuditFactory.Create(
            "EVT-" + Guid.NewGuid().ToString("N"),
            "AUD-" + Guid.NewGuid().ToString("N"),
            eventType,
            entityRef,
            entityDigest,
            taskId,
            atUtc,
            Environment.UserName,
            Environment.MachineName,
            "V04-RUNNER",
            previous?.AuditId);
        await store.AppendScenarioPackAuditEventAsync(append.Event, append.Audit);
    }

    private static void Write<T>(T value, bool json)
    {
        Console.WriteLine(JsonSerializer.Serialize(value, json ? FoundationJson.CreateOptions() : IndentedJsonOptions));
    }

    private static JsonSerializerOptions CreateIndentedOptions()
    {
        JsonSerializerOptions options = FoundationJson.CreateOptions();
        options.WriteIndented = true;
        return options;
    }
}
